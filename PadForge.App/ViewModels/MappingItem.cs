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

            // Primary source Kind holder (#111 follow-up). Reuses the per-source
            // MappingSourceItem so the primary can be Ramped / Incremental /
            // InvertOnHold with the same UI and recording as extra sources.
            _primaryKindSource = new MappingSourceItem { ParentMappingItem = this };
            _primaryKindSource.PropertyChanged += OnPrimaryKindSourcePropertyChanged;
        }

        // ─────────────────────────────────────────────
        //  ExtraSources wiring
        //
        //  Each ExtraSource (MappingSourceItem) gets:
        //    - ParentTargetIsDiscrete pushed from this row so the
        //      per-source deadzone visibility tracks the target type.
        //    - SelectedInput synced against the row's cross-device
        //      AvailableInputs list whenever the user adds a source
        //      or this row's AvailableInputs gets rebuilt.
        // ─────────────────────────────────────────────

        private void OnExtraSourcesCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(IsMultiSource));
            OnPropertyChanged(nameof(HasExtraSources));
            OnPropertyChanged(nameof(HasAnySource));
            OnPropertyChanged(nameof(VariableCount));
            OnPropertyChanged(nameof(ShouldShowEmptyDirectionHint));
            OnPropertyChanged(nameof(ShouldShowCustomExpression));
            OnPropertyChanged(nameof(ShouldShowTrimSettings));
            // Source count changed → custom-expression warning state
            // may have flipped.
            OnPropertyChanged(nameof(IsCombineExpressionWarning));
            OnPropertyChanged(nameof(IsTrivialDirect));
            RefreshVariableAliases();
            // Adding/removing a secondary can gate InvertOnHold in the primary
            // dropdown on or off (#111 audit C).
            EnforcePrimaryKindGate();

            // Clear() raises Reset with no OldItems, which would skip the
            // per-item unsubscribes below. Mirrors the tracked-list idiom
            // from PadViewModel.OnMappingsChangedForDirectCount.
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (var hooked in _extraSourcesHooked)
                {
                    hooked.PropertyChanged -= OnExtraSourcePropertyChanged;
                    hooked.ParentMappingItem = null;
                }
                _extraSourcesHooked.Clear();
                foreach (var msi in ExtraSources)
                {
                    if (msi == null) continue;
                    msi.ParentMappingItem = this;
                    msi.PropertyChanged += OnExtraSourcePropertyChanged;
                    _extraSourcesHooked.Add(msi);
                    RefreshExtraSourceInputs(msi);
                }
                return;
            }

            if (e.NewItems != null)
            {
                // First add transitions the row from single→multi-source.
                // Auto-pick a sensible combine mode if the user (or
                // legacy XML) hasn't set one yet, so the dropdown
                // never shows blank for a multi-source row.
                if (ExtraSources.Count > 0)
                    EnsureCombineModeDefault();

                foreach (var added in e.NewItems)
                {
                    if (added is MappingSourceItem msi)
                    {
                        msi.ParentMappingItem = this;
                        msi.PropertyChanged += OnExtraSourcePropertyChanged;
                        _extraSourcesHooked.Add(msi);
                        RefreshExtraSourceInputs(msi);
                    }
                }
            }
            if (e.OldItems != null)
            {
                foreach (var removed in e.OldItems)
                {
                    if (removed is MappingSourceItem msi)
                    {
                        msi.PropertyChanged -= OnExtraSourcePropertyChanged;
                        msi.ParentMappingItem = null;
                        _extraSourcesHooked.Remove(msi);
                    }
                }
            }
        }

        // Subscribed extras tracked explicitly because Reset carries no
        // OldItems (see OnExtraSourcesCollectionChanged).
        private readonly System.Collections.Generic.List<MappingSourceItem> _extraSourcesHooked
            = new System.Collections.Generic.List<MappingSourceItem>();

        private void OnExtraSourcePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, nameof(MappingSourceItem.DeviceGuid), StringComparison.Ordinal)
                && sender is MappingSourceItem msi)
            {
                RefreshExtraSourceInputs(msi);
            }
            // A secondary toggling to/from InvertOnHold changes whether it
            // contributes, which gates InvertOnHold in the primary dropdown (#111 audit C).
            if (string.Equals(e.PropertyName, nameof(MappingSourceItem.Kind), StringComparison.Ordinal))
            {
                EnforcePrimaryKindGate();
            }
            // Any change that affects the source's friendly label
            // ripples to the variable-alias display for that position.
            if (e.PropertyName == nameof(MappingSourceItem.Descriptor)
                || e.PropertyName == nameof(MappingSourceItem.DeviceLabel)
                || e.PropertyName == nameof(MappingSourceItem.SelectedInput))
            {
                RefreshVariableAliases();
            }
            // Binding or clearing any of a secondary's feeds (descriptor,
            // Up/Down keys, modifier key) or flipping its kind can change
            // whether anything feeds the row at all.
            if (e.PropertyName == nameof(MappingSourceItem.Descriptor)
                || e.PropertyName == nameof(MappingSourceItem.ParamUp)
                || e.PropertyName == nameof(MappingSourceItem.ParamDown)
                || e.PropertyName == nameof(MappingSourceItem.ParamModifier)
                || e.PropertyName == nameof(MappingSourceItem.Kind))
            {
                OnPropertyChanged(nameof(HasAnySource));
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
            msi.ParentMappingItem = this;
            msi.ParentTargetIsDiscrete = IsTargetDiscrete;
            msi.SyncSelectedInputFromState(AvailableInputs);
            // ParamUp/Down/Modifier picker bridges resolve their
            // InputChoice against this row's AvailableInputs — re-fire
            // the picker getters whenever the input list is rebuilt.
            msi.RefreshParamPickerChoices();
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
                    || t.StartsWith("RawAxis", StringComparison.Ordinal)
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
            RefreshExtraSourceInputs(PrimaryKindSource);
            foreach (var msi in ExtraSources)
                RefreshExtraSourceInputs(msi);
        }

        // ─────────────────────────────────────────────
        //  Primary source Kind (#111 follow-up)
        //
        //  The primary mapping can use Incremental / Ramped / InvertOnHold,
        //  not only Direct. The Kind and its kind-specific params live on a
        //  reused MappingSourceItem so the dropdown, cards, cross-device key
        //  pickers, and recording all come for free. For Direct the primary
        //  descriptor still lives in SourceDescriptor; for the stateful kinds
        //  the descriptor is unused and PrimaryKindSource drives the row.
        // ─────────────────────────────────────────────

        private MappingSourceItem _primaryKindSource;
        public MappingSourceItem PrimaryKindSource
        {
            get => _primaryKindSource;
            private set => SetProperty(ref _primaryKindSource, value);
        }

        /// <summary>True when the primary uses the plain Direct descriptor (the
        /// default). Gates the Source-column picker (shown) vs the kind cards
        /// (hidden) in the row-detail strip.</summary>
        public bool IsPrimaryDirect =>
            string.Equals(PrimaryKindSource?.Kind ?? "Direct", "Direct", StringComparison.Ordinal);

        /// <summary>Friendly name of the primary's current Kind (e.g. "Ramp"), for
        /// any compact display when the primary is non-Direct.</summary>
        public string PrimaryKindLabel
        {
            get
            {
                var k = PrimaryKindSource?.Kind ?? "Direct";
                foreach (var opt in MappingSourceItem.KindOptions)
                    if (string.Equals(opt.Value, k, StringComparison.Ordinal))
                        return opt.Name;
                return k;
            }
        }

        /// <summary>True when the row has at least one secondary source that
        /// contributes a value to the combine (any kind other than InvertOnHold,
        /// which is a pure row modifier). InvertOnHold as the primary only does
        /// something when such a secondary exists for it to flip.</summary>
        private bool HasContributingExtraSource
        {
            get
            {
                foreach (var s in ExtraSources)
                    if (s != null && !string.Equals(s.Kind ?? "Direct", "InvertOnHold", StringComparison.Ordinal))
                        return true;
                return false;
            }
        }

        /// <summary>Kind choices offered in the PRIMARY mode dropdown. Same as
        /// <see cref="MappingSourceItem.KindOptions"/>, but InvertOnHold is hidden
        /// unless the row has a contributing secondary source (#111 audit C). As a
        /// solo primary, InvertOnHold flips nothing, so offering it would invite a
        /// mapping that silently does nothing. The extra-source dropdown still binds
        /// the full list, since that is where InvertOnHold belongs.</summary>
        public System.Collections.Generic.IReadOnlyList<MappingSourceItem.KindChoice> PrimaryKindOptions
        {
            get
            {
                var all = MappingSourceItem.KindOptions;
                if (HasContributingExtraSource) return all;
                var filtered = new System.Collections.Generic.List<MappingSourceItem.KindChoice>(all.Count);
                foreach (var k in all)
                    if (!string.Equals(k.Value, "InvertOnHold", StringComparison.Ordinal))
                        filtered.Add(k);
                return filtered;
            }
        }

        /// <summary>Keeps the primary off an option the gate no longer offers. When
        /// the last contributing secondary goes away while the primary is InvertOnHold,
        /// the primary would be inert, so revert it to Direct. Re-fires the options so
        /// the dropdown updates.</summary>
        /// <summary>Set while a reload is repopulating this row. The gate
        /// below reads ExtraSources, and a reload empties that collection
        /// before refilling it, so every firing point saw "no contributing
        /// secondary" mid-load and reverted a perfectly valid stored
        /// InvertOnHold primary to Direct. The loader brackets the row and
        /// runs the gate once at the end, against the finished state.</summary>
        private bool _suppressPrimaryKindGate;

        internal void BeginLoadRow() => _suppressPrimaryKindGate = true;

        internal void EndLoadRow()
        {
            _suppressPrimaryKindGate = false;
            EnforcePrimaryKindGate();
        }

        private void EnforcePrimaryKindGate()
        {
            if (_suppressPrimaryKindGate) return;
            if (!HasContributingExtraSource
                && string.Equals(PrimaryKindSource?.Kind ?? "Direct", "InvertOnHold", StringComparison.Ordinal))
            {
                PrimaryKindSource.Kind = "Direct";
            }
            OnPropertyChanged(nameof(PrimaryKindOptions));
        }

        private void OnPrimaryKindSourcePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MappingSourceItem.Kind))
            {
                OnPropertyChanged(nameof(IsPrimaryDirect));
                OnPropertyChanged(nameof(IsMultiSource));
                OnPropertyChanged(nameof(PrimaryKindLabel));
                OnPropertyChanged(nameof(IsTrivialDirect));
                OnPropertyChanged(nameof(HasAnySource));
                // A primary set (or loaded) as InvertOnHold with no contributing
                // secondary is inert; revert it to Direct (#111 audit C).
                EnforcePrimaryKindGate();
            }
            if (string.Equals(e.PropertyName, nameof(MappingSourceItem.DeviceGuid), StringComparison.Ordinal)
                && sender is MappingSourceItem msi)
            {
                RefreshExtraSourceInputs(msi);
            }
            // A stateful primary's feeds are the Up/Down/Modifier keys on
            // the kind holder; binding or clearing one flips HasAnySource
            // where SourceDescriptor cannot.
            if (e.PropertyName == nameof(MappingSourceItem.Descriptor)
                || e.PropertyName == nameof(MappingSourceItem.ParamUp)
                || e.PropertyName == nameof(MappingSourceItem.ParamDown)
                || e.PropertyName == nameof(MappingSourceItem.ParamModifier))
            {
                OnPropertyChanged(nameof(HasAnySource));
            }
        }

        /// <summary>Hydrates the reused <see cref="PrimaryKindSource"/> from a stored
        /// <see cref="Engine.Data.MappingSource"/> on load. Copies into the existing
        /// object so its PropertyChanged wiring (recording, dirty, picker refresh)
        /// survives. A null or Direct source resets it to a plain Direct holder so the
        /// row falls back to its <see cref="SourceDescriptor"/> primary.</summary>
        public void LoadPrimaryKind(Engine.Data.MappingSource src)
        {
            var p = PrimaryKindSource;
            if (p == null) return;
            if (src == null || string.Equals(src.Kind ?? "Direct", "Direct", StringComparison.Ordinal))
            {
                p.ParamUp = "";
                p.ParamDown = "";
                p.ParamModifier = "";
                p.Kind = "Direct";
                return;
            }
            p.DeviceGuid = src.DeviceGuid ?? "";
            p.ParamUp = src.ParamUp ?? "";
            p.ParamDown = src.ParamDown ?? "";
            p.ParamRate = src.ParamRate;
            p.ParamSticky = src.ParamSticky;
            p.ParamMin = src.ParamMin;
            p.ParamMax = src.ParamMax;
            p.ParamModifier = src.ParamModifier ?? "";
            p.ParamAttackTime = src.ParamAttackTime;
            p.ParamReleaseTime = src.ParamReleaseTime;
            p.ParamAutocenter = src.ParamAutocenter;
            p.ParamReverseMultiplier = src.ParamReverseMultiplier >= 1 ? src.ParamReverseMultiplier : 4.0;
            p.Kind = src.Kind; // set Kind last so card visibility settles after params load
            RefreshExtraSourceInputs(p);
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
                    OnPropertyChanged(nameof(HasAnySource));
                    OnPropertyChanged(nameof(IsDeadZoneApplicable));
                    OnPropertyChanged(nameof(IsHalfAxisApplicable));
                    OnPropertyChanged(nameof(IsGyroSource));
                    OnPropertyChanged(nameof(IsMouseCursorSource));
                    OnPropertyChanged(nameof(IsIrPointerSource));
                    OnPropertyChanged(nameof(IsMouseMotionSource));
                    OnPropertyChanged(nameof(IsGenericSensitivitySource));
                    OnPropertyChanged(nameof(ShouldShowEmptyDirectionHint));
                    // Toggling the primary source flips the row's
                    // effective source count, which can change whether
                    // a custom formula's `a` reference is in range.
                    OnPropertyChanged(nameof(IsCombineExpressionWarning));
                    OnPropertyChanged(nameof(IsTrivialDirect));
                    RefreshVariableAliases();
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
                    OnPropertyChanged(nameof(HasAnySource));
                    OnPropertyChanged(nameof(ShouldShowEmptyDirectionHint));
                    OnPropertyChanged(nameof(IsTrivialDirect));
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

        /// <summary>
        /// Whether anything feeds this row: a Direct primary descriptor, a
        /// bound stateful primary (Ramp / Incremental / InvertOnHold, whose
        /// feeds are the Up/Down/Modifier keys on
        /// <see cref="PrimaryKindSource"/>, not a descriptor), or any extra
        /// source with a bound feed. The preview annotation layer keys chip
        /// presence on this rather than <see cref="IsMapped"/>, so
        /// stateful-primary rows stay visible.
        /// </summary>
        public bool HasAnySource
        {
            get
            {
                if (IsMapped) return true;
                if (!IsPrimaryDirect && PrimaryKindSource != null && PrimaryKindSource.HasAnyBoundFeed)
                    return true;
                foreach (var s in ExtraSources)
                    if (s != null && s.HasAnyBoundFeed)
                        return true;
                return false;
            }
        }

        private void OnCultureChanged()
        {
            OnPropertyChanged(nameof(SourceDisplayText));
            OnPropertyChanged(nameof(RecordButtonText));
            // The Kind pickers' option labels are localized. The list getter
            // is culture-aware (LCID-keyed cache), but nothing re-read it on
            // a live language switch, so the dropdowns kept the old language
            // (owner report 2026-07-16, found alongside the Menus-tab twin).
            OnPropertyChanged(nameof(PrimaryKindOptions));
            foreach (var src in ExtraSources)
                src.RefreshCulture();
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
                        // E7 authoring default: see the shared helper.
                        ApplyScrollUpAuthoringDefault(value.Descriptor);
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
                else if (clean.StartsWith("I", StringComparison.OrdinalIgnoreCase) && clean.Length > 1 && !char.IsDigit(clean[1])
                         && !PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(clean))
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
                    // Straight guid equality, INCLUDING the empty guid: an
                    // empty-guid ("any device") source genuinely matches
                    // the picker's empty-guid "(Any device)" entry, so a
                    // device-agnostic mapping resolves into that group
                    // instead of borrowing the first concrete device's
                    // entry and rendering under its header.
                    if (string.Equals(choice.DeviceGuid ?? "", wantGuid, StringComparison.OrdinalIgnoreCase))
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

        private bool _isInputActive;

        /// <summary>True while live input flows on this row (#175 rowfire).
        /// Set by InputService from the numeric value with a deadband, so
        /// stick rows at rest stay dark.</summary>
        public bool IsInputActive
        {
            get => _isInputActive;
            set => SetProperty(ref _isInputActive, value);
        }

        /// <summary>True when the row is a plain one-liner (#175 telemetry
        /// board): a single Direct primary source, no negative-direction
        /// pair, no invert / half / bidirectional option, and no custom
        /// combine formula. Trivial rows render as a compact mono line in
        /// the Mappings grid so a whole config reads on one screen;
        /// anything richer keeps the full editing row. Re-notified from
        /// every contributing setter.</summary>
        public bool IsTrivialDirect =>
            !string.IsNullOrEmpty(_sourceDescriptor)
            && string.IsNullOrEmpty(_negSourceDescriptor)
            && IsPrimaryDirect
            && ExtraSources.Count == 0
            && !_isInverted && !_isHalfAxis && !_isBidirectional
            && !(IsCustomCombine && !string.IsNullOrWhiteSpace(_combineExpression));

        private bool _isExpandedOverride;

        /// <summary>Per-row escape hatch from the compact trivial rendering
        /// (#175 telemetry board). Set by the Pad page's row click handler
        /// so a compact row opens back into the full editing row; cleared
        /// when the row is deselected. UI-only state, never persisted.</summary>
        public bool IsExpandedOverride
        {
            get => _isExpandedOverride;
            set => SetProperty(ref _isExpandedOverride, value);
        }

        private bool _isRowSelected;

        /// <summary>Mirror of the row's DataGrid selection state (#175 phase
        /// two item 10). Written by the Pad page's SelectionChanged handler
        /// so the row template's compact-swap trigger can condition on
        /// selection with a plain DataContext binding. UI-only state, never
        /// persisted.</summary>
        public bool IsRowSelected
        {
            get => _isRowSelected;
            set => SetProperty(ref _isRowSelected, value);
        }

        /// <summary>True when the row actually fans in extra sources (#175
        /// phase two item 10). Narrower than <see cref="IsMultiSource"/>,
        /// which is also true for a non-Direct primary with no extras.
        /// Those rows keep their full cells (the Up/Down pickers live
        /// there), so the collapsed one-line rendering keys off this.</summary>
        public bool HasExtraSources => ExtraSources.Count > 0;

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
            else if (d.StartsWith("I", StringComparison.OrdinalIgnoreCase) && d.Length > 1 && !char.IsDigit(d[1])
                     && !PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(d))
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

        /// <summary>E7 authoring default (2026-07-14 audit, closed
        /// 2026-07-18), the ONE seam shared by the dropdown pick and the
        /// recorder: the wire contract makes an un-inverted press on the
        /// vertical scroll target read DOWN (Step 3's documented negation,
        /// which the Workshop translator compensates for with SCROLL_UP =>
        /// Invert). A user attaching a plain press expects scroll UP, so
        /// direction-free press-class sources (buttons, POV directions,
        /// touchpad clicks / finger downs) get Invert stamped as the
        /// authoring default. Hydration and existing rows are untouched
        /// (no migration), the user can uncheck for scroll down, and
        /// KbmScrollNeg (the explicit down leg) plus horizontal scroll
        /// are never stamped.</summary>
        public void ApplyScrollUpAuthoringDefault(string descriptor)
        {
            if (!string.Equals(TargetSettingName, "KbmScroll", StringComparison.Ordinal)) return;
            var cls = PadForge.Engine.Common.Mapping.SourceCoercion.ClassifyDescriptor(descriptor ?? "");
            if (cls == PadForge.Engine.Common.Mapping.SourceCoercion.SourceType.Button
                || cls == PadForge.Engine.Common.Mapping.SourceCoercion.SourceType.PovDirection
                || cls == PadForge.Engine.Common.Mapping.SourceCoercion.SourceType.TouchpadButton
                || cls == PadForge.Engine.Common.Mapping.SourceCoercion.SourceType.NfcTag)
                IsInverted = true;
        }

        /// <summary>The primary descriptor with any legacy I/H invert/half
        /// prefix removed (same parse as <see cref="LoadDescriptor"/> and
        /// <see cref="RebuildDescriptor"/>). The primary KEEPS the encoded
        /// form ("IAxis 2") while the flags are set, so every family
        /// predicate below must test grammar on this body; testing the raw
        /// string made the matching slider/checkbox vanish the moment the
        /// user checked Invert or Half.</summary>
        private static string StripLegacyPrefix(string descriptor)
        {
            string d = descriptor ?? string.Empty;
            if (d.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
                return d.Substring(2);
            if (d.StartsWith("I", StringComparison.OrdinalIgnoreCase) && d.Length > 1 && !char.IsDigit(d[1])
                && !PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(d))
                return d.Substring(1);
            if (d.StartsWith("H", StringComparison.OrdinalIgnoreCase) && d.Length > 1 && !char.IsDigit(d[1]))
                return d.Substring(1);
            return d;
        }

        /// <summary>
        /// Loads a negative-direction descriptor, parsing any I/H prefixes.
        /// </summary>
        public void LoadNegDescriptor(string descriptor)
        {
            NegSourceDescriptor = descriptor ?? string.Empty;
        }

        /// <summary>Issue #61 — promotes the legacy
        /// <see cref="NegSourceDescriptor"/> into a visible
        /// <see cref="ExtraSources"/> entry with Invert flipped.
        /// Called by the recording pipeline so the user sees both
        /// directions immediately after a two-phase recording, instead
        /// of having to toggle the Device dropdown to trigger a load-
        /// time migration. The new ExtraSource inherits its device
        /// origin from <see cref="PrimarySourceDeviceGuid"/>, which
        /// <c>RecorderService.CompleteRecording</c> reliably stamps
        /// with the device that physically fired. Does nothing when
        /// the Neg is empty or when an equivalent extra already exists
        /// on the row.</summary>
        public void PromoteNegDescriptorToExtraSource()
        {
            string neg = NegSourceDescriptor;
            if (string.IsNullOrEmpty(neg)) return;

            // Strip the legacy I/H prefix off the descriptor — the
            // new schema stores Invert/HalfAxis as separate flags.
            bool inv = false, half = false;
            string clean = neg;
            if (clean.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
            { inv = true; half = true; clean = clean.Substring(2); }
            else if (clean.StartsWith("I", StringComparison.OrdinalIgnoreCase) && clean.Length > 1 && !char.IsDigit(clean[1])
                     && !PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(clean))
            { inv = true; clean = clean.Substring(1); }
            else if (clean.StartsWith("H", StringComparison.OrdinalIgnoreCase) && clean.Length > 1 && !char.IsDigit(clean[1]))
            { half = true; clean = clean.Substring(1); }

            // The Neg pair's effective Invert is FLIPPED relative to
            // the primary's invert encoding — same convention as the
            // save path's bipolar-pair emission.
            bool effectiveInvert = !inv;
            string deviceGuid = PrimarySourceDeviceGuid ?? "";
            string deviceLabel = PrimarySourceDeviceLabel ?? "";

            // Skip if an equivalent extra is already present
            // (idempotent for repeat calls).
            foreach (var existing in ExtraSources)
            {
                if (existing == null) continue;
                if (string.Equals(existing.Descriptor ?? "", clean, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.DeviceGuid ?? "", deviceGuid, StringComparison.OrdinalIgnoreCase)
                    && existing.Invert == effectiveInvert)
                    return;
            }

            ExtraSources.Insert(0, new MappingSourceItem
            {
                Kind = "Direct",
                DeviceGuid = deviceGuid,
                DeviceLabel = deviceLabel,
                Descriptor = clean,
                Invert = effectiveInvert,
                HalfAxis = half,
                DeadZone = MappingDeadZone,
            });

            // Clear the legacy field so the save path doesn't double-
            // emit the Neg (once from NegSourceDescriptor, once from
            // ExtraSources). The freshly-inserted ExtraSource is the
            // sole carrier going forward.
            NegSourceDescriptor = string.Empty;
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
                    OnPropertyChanged(nameof(IsTrivialDirect));
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
                {
                    RebuildDescriptor();
                    OnPropertyChanged(nameof(IsTrivialDirect));
                    OnPropertyChanged(nameof(IsInvertApplicable));
                }
            }
        }

        /// <summary>Whether the Invert checkbox actually does anything in
        /// the current combination (round eight, R15): with Half + Either
        /// both on, the engine reads absolute deflection and Invert is
        /// documented inert, yet the checkbox was offered ungated while
        /// both its neighbours carry applicability gates. Drives the
        /// checkbox's IsEnabled so an inert option is visibly inert.</summary>
        public bool IsInvertApplicable => !(_isHalfAxis && _isBidirectional);

        private bool _isBidirectional;

        /// <summary>When <c>true</c> AND <see cref="IsHalfAxis"/> is also on,
        /// the axis-to-button check fires on absolute deflection past the
        /// deadzone — either side of center counts. <see cref="IsInverted"/>
        /// has no effect in this mode (mirroring around center already
        /// covers both directions). Persisted via the per-row
        /// <c>PadSetting.MappingBidirectional</c> dictionary rather than a
        /// descriptor prefix, since I/H prefixes are already consumed and a
        /// third boolean would need a clean storage slot.</summary>
        public bool IsBidirectional
        {
            get => _isBidirectional;
            set
            {
                if (SetProperty(ref _isBidirectional, value))
                {
                    OnPropertyChanged(nameof(IsTrivialDirect));
                    OnPropertyChanged(nameof(IsInvertApplicable));
                }
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
            // Minimum 1: a 0% axis-to-button deadzone is disallowed (0 was treated
            // as "unset" downstream and reverted to the 50% default).
            get => _mappingDeadZone;
            set => SetProperty(ref _mappingDeadZone, Math.Clamp(value, 1, 100));
        }

        private double _gyroSensitivity = 1.0;
        private double _mouseCursorSensitivity = 1.0;

        /// <summary>Per-source mouse-cursor sensitivity for the primary source
        /// (issue #107). Mirrors <see cref="MappingSourceItem.MouseCursorSensitivity"/>,
        /// applied only when the primary descriptor is a "Mouse Position X/Y" axis
        /// (see <see cref="IsMouseCursorSource"/>). 1.0 = full deflection at 10% of
        /// screen width from center.</summary>
        public double MouseCursorSensitivity
        {
            get => _mouseCursorSensitivity;
            set => SetProperty(ref _mouseCursorSensitivity, Math.Clamp(value, 0.1, 5.0));
        }

        private double _irPointerSensitivity = 1.0;
        /// <summary>Per-source Wii IR-pointer sensitivity for the primary source
        /// (issue #146). Mirrors <see cref="MappingSourceItem.IrPointerSensitivity"/>,
        /// applied only when the primary descriptor is an "IR Pointer X/Y" axis.</summary>
        public double IrPointerSensitivity
        {
            get => _irPointerSensitivity;
            set => SetProperty(ref _irPointerSensitivity, Math.Clamp(value, 0.1, 5.0));
        }

        private double _sensitivity = 1.0;
        /// <summary>Generic per-source sensitivity for the primary source
        /// (issue #9). Mirrors <see cref="MappingSourceItem.Sensitivity"/>;
        /// applied only when the primary descriptor is a plain analog source
        /// (see <see cref="IsGenericSensitivitySource"/>). 1.0 = unchanged.</summary>
        public double Sensitivity
        {
            get => _sensitivity;
            set => SetProperty(ref _sensitivity, Math.Clamp(value, 0.1, 5.0));
        }

        /// <summary>True when the primary source descriptor is an absolute cursor
        /// axis ("Mouse Position X/Y"). Mirrors
        /// <see cref="MappingSourceItem.IsMouseCursorSource"/>.</summary>
        public bool IsMouseCursorSource => !string.IsNullOrEmpty(_sourceDescriptor)
            && StripLegacyPrefix(_sourceDescriptor).StartsWith("Mouse Position ", StringComparison.Ordinal);

        /// <summary>True when the primary source descriptor is a Wii IR pointer axis
        /// ("IR Pointer X/Y", issue #146). Mirrors
        /// <see cref="MappingSourceItem.IsIrPointerSource"/>.</summary>
        public bool IsIrPointerSource => !string.IsNullOrEmpty(_sourceDescriptor)
            && StripLegacyPrefix(_sourceDescriptor).StartsWith("IR Pointer ", StringComparison.Ordinal);

        /// <summary>True for a "Mouse Motion X/Y" primary source (#154).
        /// Mirrors <see cref="MappingSourceItem.IsMouseMotionSource"/> so the
        /// per-source sensitivity slider's visibility binding resolves on the
        /// legacy-row template too (a missing property leaves the failed
        /// binding at Visibility's default, which is Visible on EVERY row).</summary>
        public bool IsMouseMotionSource => !string.IsNullOrEmpty(_sourceDescriptor)
            && StripLegacyPrefix(_sourceDescriptor).StartsWith("Mouse Motion ", StringComparison.Ordinal);

        /// <summary>Per-source gyro sensitivity multiplier for the primary
        /// source. Mirrors <see cref="MappingSourceItem.GyroSensitivity"/>;
        /// applied only when the primary descriptor is a Gyro axis (see
        /// <see cref="IsGyroSource"/>). 1.0 = the engine's default
        /// 500°/s → ±1 deflection scale.</summary>
        public double GyroSensitivity
        {
            get => _gyroSensitivity;
            set => SetProperty(ref _gyroSensitivity, Math.Clamp(value, 0.1, 10.0));
        }

        /// <summary>True when the primary source descriptor is a gyro
        /// axis ("Gyro Pitch" / "Gyro Yaw" / "Gyro Roll" / horizontal blend).
        /// Mirrors <see cref="MappingSourceItem.IsGyroSource"/> so the
        /// primary's gyro-sensitivity slider can be gated identically.</summary>
        public bool IsGyroSource => !string.IsNullOrEmpty(_sourceDescriptor)
            && StripLegacyPrefix(_sourceDescriptor).StartsWith("Gyro ", StringComparison.Ordinal);

        /// <summary>True when the primary source carries the generic per-source
        /// Sensitivity knob (issue #9): a plain "Axis N" / "Slider N" read or an
        /// abstract Gamepad stick / trigger that canonicalizes to one. Mirrors
        /// <see cref="MappingSourceItem.IsGenericSensitivitySource"/> so the
        /// primary's slider is gated identically, mutually exclusive with the
        /// specialized-family predicates above.</summary>
        public bool IsGenericSensitivitySource =>
            PadForge.Engine.Common.Mapping.SourceCoercion.IsGenericSensitivityDescriptor(
                StripLegacyPrefix(_sourceDescriptor));

        /// <summary>
        /// True when the deadzone column is applicable for this row:
        /// the source is an axis/slider AND the target is a discrete output
        /// (button, d-pad, POV, key, note) — NOT an axis-to-axis mapping.
        /// </summary>
        public bool IsDeadZoneApplicable
        {
            get
            {
                // Check source is axis/slider, on the prefix-stripped body
                // (the primary keeps the legacy I/H encoding).
                var desc = StripLegacyPrefix(_sourceDescriptor);
                if (string.IsNullOrEmpty(desc)) return false;

                // Engine-owned continuous families whose button-thresholding
                // reads the per-source DeadZone (same set the grid's
                // MappingSourceItem.IsDeadZoneApplicable exposes, #154).
                bool engineFamily =
                    desc.StartsWith("Mouse Motion ", StringComparison.Ordinal)
                    || desc.StartsWith("IR Pointer ", StringComparison.Ordinal)
                    || desc.StartsWith("IR Brightness", StringComparison.Ordinal)
                    || desc.StartsWith("Balance ", StringComparison.Ordinal)
                    // Absolute pointer (#9 B-15): continuous position whose
                    // button coercion thresholds on the per-source DeadZone
                    // like the IR pointer (same set the grid's
                    // MappingSourceItem.IsDeadZoneApplicable exposes), so it
                    // joins the family list ahead of the blanket Touchpad
                    // exclusion below.
                    || PadForge.Engine.Common.Mapping.SourceCoercion.IsTouchpadPointerDescriptor(desc)
                    // Pressure (#239): the bool coercion thresholds on the
                    // per-source DeadZone (whole pad or zone-windowed), so
                    // it joins the family ahead of the blanket exclusion.
                    || PadForge.Engine.Common.Mapping.SourceCoercion.IsTouchpadPressureDescriptor(desc);
                if (!engineFamily)
                {
                    // Touchpad finger X/Y joined the generic Sensitivity
                    // family (#9 B-13) but have no axis-to-button threshold
                    // read (ReadAsBool's touchpad branch reads Click /
                    // "Finger M Down" / Ring / Pressure only, and Pressure
                    // is family-listed above), so the remaining touchpad
                    // descriptors keep the pre-widening hidden column.
                    if (desc.StartsWith("Touchpad ", StringComparison.Ordinal))
                        return false;
                    // Covers "Axis N" / "Slider N" plus the abstract Gamepad
                    // sticks / triggers that canonicalize to one (#9).
                    if (!PadForge.Engine.Common.Mapping.SourceCoercion.IsGenericSensitivityDescriptor(desc))
                        return false;
                }

                // Check target is a discrete (button-type) output, not an axis.
                var t = TargetSettingName;
                if (t.Contains("ThumbAxis") || t.StartsWith("RawAxis")
                    || t.StartsWith("KbmMouse") || t.StartsWith("KbmScroll")
                    || t.StartsWith("MidiCC"))
                    return false;
                if (t == "LeftTrigger" || t == "RightTrigger")
                    return false;

                return true;
            }
        }

        /// <summary>
        /// True when the Half checkbox (and the dependent Either) is
        /// meaningful for this row's source. Half-axis only applies to
        /// continuous-range sources: Axis, Slider, Touchpad X/Y/Pressure,
        /// and Gyro Pitch/Yaw/Roll. Discrete sources (Button, POV
        /// direction, Touchpad Click / Finger Down) have no upper or
        /// lower half to pick.
        /// </summary>
        public bool IsHalfAxisApplicable
        {
            get
            {
                // Evaluate on the prefix-stripped body: the primary keeps
                // the legacy I/H encoding while the flags are set, which
                // otherwise hid the checkbox for every family below the
                // moment Invert was checked.
                var desc = StripLegacyPrefix(_sourceDescriptor);
                if (string.IsNullOrEmpty(desc)) return false;

                // Gyro is always axis-like.
                if (desc.StartsWith("Gyro ", StringComparison.Ordinal))
                    return true;

                // Joy-Con 2 mouse motion is a signed velocity: Half picks one
                // direction, Invert chooses which (#154, mirrors the grid's
                // MappingSourceItem.IsHalfAxisApplicable).
                if (desc.StartsWith("Mouse Motion ", StringComparison.Ordinal))
                    return true;

                // Touchpad: X / Y / Pressure (whole-pad or the #9 B-1
                // half-windowed X/Y variants) are continuous; Click and
                // Finger Down (windowed included) are discrete. The finger
                // predicate covers the axis spellings, halves included,
                // and never matches the bool forms.
                if (desc.StartsWith("Touchpad ", StringComparison.Ordinal))
                {
                    return PadForge.Engine.Common.Mapping.SourceCoercion
                            .IsTouchpadFingerAxisDescriptor(desc)
                        || desc.EndsWith(" Pressure", StringComparison.Ordinal);
                }

                // "Axis N" / "Slider N" plus the abstract Gamepad sticks /
                // triggers that canonicalize to one (#9).
                return PadForge.Engine.Common.Mapping.SourceCoercion.IsGenericSensitivityDescriptor(desc);
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

            // Engine-owned families ("IR Pointer X/Y", "IR Brightness") do not
            // use the legacy I/H prefix encoding: their names legitimately start
            // with 'I', so stripping would corrupt them and prefixing would
            // produce an unrecognizable "IIR Pointer X". Their Invert rides the
            // mapping-set row flag instead.
            if (PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(_sourceDescriptor))
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
                IsBidirectional = false;
                MappingDeadZone = 50;
                PrimarySourceDeviceGuid = "";
                PrimarySourceDeviceLabel = "";
                // Also wipe a non-Direct primary kind (#111): empty its Up/Down/
                // Modifier keys and drop back to Direct so Clear leaves nothing
                // assigned, whether the primary is a plain descriptor or a kind.
                var p = PrimaryKindSource;
                if (p != null)
                {
                    p.ParamUp = "";
                    p.ParamDown = "";
                    p.ParamModifier = "";
                    p.Kind = "Direct";
                }
                SyncSelectedInputFromDescriptor();
            });

        private RelayCommand _resetDeadZoneCommand;

        /// <summary>Command to reset the per-mapping deadzone to default (50%).</summary>
        public RelayCommand ResetDeadZoneCommand =>
            _resetDeadZoneCommand ??= new RelayCommand(() => MappingDeadZone = 50);

        private RelayCommand _resetMouseCursorSensitivityCommand;
        /// <summary>Resets the primary source's mouse-cursor sensitivity to 1.0 (#107).</summary>
        public RelayCommand ResetMouseCursorSensitivityCommand =>
            _resetMouseCursorSensitivityCommand ??= new RelayCommand(() => MouseCursorSensitivity = 1.0);

        private RelayCommand _resetIrPointerSensitivityCommand;
        /// <summary>Resets the primary source's IR-pointer sensitivity to 1.0 (#146).</summary>
        public RelayCommand ResetIrPointerSensitivityCommand =>
            _resetIrPointerSensitivityCommand ??= new RelayCommand(() => IrPointerSensitivity = 1.0);

        private RelayCommand _resetGyroSensitivityCommand;
        /// <summary>Resets the primary source's gyro sensitivity to 1.0.</summary>
        public RelayCommand ResetGyroSensitivityCommand =>
            _resetGyroSensitivityCommand ??= new RelayCommand(() => GyroSensitivity = 1.0);

        private RelayCommand _resetSensitivityCommand;
        /// <summary>Resets the primary source's generic sensitivity to 1.0 (#9).</summary>
        public RelayCommand ResetSensitivityCommand =>
            _resetSensitivityCommand ??= new RelayCommand(() => Sensitivity = 1.0);

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

        /// <summary>True when this row's Target is one of the touchpad
        /// X/Y position axes (TouchpadX1/Y1/X2/Y2). The Custom formula
        /// editor surfaces the <c>aD..dD</c> "is source touching" chips
        /// only on these rows — for every other target the touchpad-
        /// passthrough gated evaluator never runs and aD-style references
        /// would always resolve to 0, so the chips would just be confusing.</summary>
        public bool IsTouchpadAxisTarget =>
            string.Equals(TargetSettingName, "TouchpadX1", StringComparison.Ordinal)
         || string.Equals(TargetSettingName, "TouchpadY1", StringComparison.Ordinal)
         || string.Equals(TargetSettingName, "TouchpadX2", StringComparison.Ordinal)
         || string.Equals(TargetSettingName, "TouchpadY2", StringComparison.Ordinal);

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
                // Abstract Gamepad button/D-pad aliases (#9) fold to their
                // canonical "Button N" / "POV 0 Dir" form first.
                var d = _sourceDescriptor.Trim();
                d = PadForge.Engine.Common.Mapping.SourceCoercion.ResolveGamepadAlias(d) ?? d;
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
            set
            {
                if (SetProperty(ref _primarySourceDeviceLabel, value ?? ""))
                    RefreshVariableAliases();
            }
        }

        private bool _noInherit;
        /// <summary>Shift-layer "do not inherit from Base" flag. Round-trips
        /// to <see cref="PadForge.Engine.Data.MappingRow.NoInherit"/> on the
        /// active layer's row. Visible only when the user is authoring a
        /// non-Base layer (the Mappings DataGrid's NoInherit cell is
        /// collapsed when <see cref="PadViewModel.ActiveLayerMask"/> ==
        /// <c>"Base"</c>).</summary>
        public bool NoInherit
        {
            get => _noInherit;
            set => SetProperty(ref _noInherit, value);
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
                    OnPropertyChanged(nameof(ShouldShowCustomExpression));
                    OnPropertyChanged(nameof(IsStickTrimCombine));
                    OnPropertyChanged(nameof(ShouldShowTrimSettings));
                    OnPropertyChanged(nameof(IsTrivialDirect));
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
                    OnPropertyChanged(nameof(IsTrivialDirect));
                }
            }
        }

        public bool IsMultiSource => ExtraSources.Count > 0 || !IsPrimaryDirect;

        /// <summary>Number of source variables the row's combine formula can
        /// reference. Primary slot (<c>a</c>) is always present, plus one
        /// per ExtraSource. Drives the chip-palette visibility so users
        /// only see letters that map to a real source.</summary>
        public int VariableCount => 1 + (ExtraSources?.Count ?? 0);
        public bool IsCustomCombine => string.Equals(_combineMode, "Custom", StringComparison.Ordinal);

        /// <summary>True only when a row has multiple sources AND the
        /// user picked Custom for the combine mode. Gates the formula
        /// editor so it disappears when the row falls back to single-
        /// source (e.g. the user removed the last extra source).</summary>
        public bool ShouldShowCustomExpression => IsMultiSource && IsCustomCombine;

        public bool IsStickTrimCombine => string.Equals(_combineMode, "StickTrim", StringComparison.Ordinal);

        /// <summary>True when this row targets a trigger-class output,
        /// the only class the engine's StickTrim combine intercepts.
        /// Gates both the dropdown entry and the trim strip so the mode
        /// is never offered where it would silently degrade to the
        /// OR/MaxAbs default (a "trim stick" on a button row would just
        /// OR into the press). Extended rows are admitted by their
        /// creation-time category: PadViewModel builds trigger-slot axis
        /// rows with MappingCategory.Triggers and stick-slot rows with
        /// the stick categories, so stick-configured Extended axes stay
        /// out.</summary>
        public bool IsTriggerTarget =>
            string.Equals(TargetSettingName, "LeftTrigger", StringComparison.Ordinal)
         || string.Equals(TargetSettingName, "RightTrigger", StringComparison.Ordinal)
         || ((TargetSettingName?.StartsWith("RawAxis", StringComparison.Ordinal) ?? false)
             && Category == MappingCategory.Triggers);

        /// <summary>Gates the Stick Trim settings strip (#155), same
        /// pattern as <see cref="ShouldShowCustomExpression"/> plus the
        /// trigger-target gate.</summary>
        public bool ShouldShowTrimSettings => IsMultiSource && IsStickTrimCombine && IsTriggerTarget;

        private int _trimDeadzone = 25;
        /// <summary>Stick-trim (#155): trim-axis deflection below this
        /// percentage is ignored (steering wobble guard).</summary>
        public int TrimDeadzone
        {
            get => _trimDeadzone;
            set => SetProperty(ref _trimDeadzone, System.Math.Clamp(value, 0, 95));
        }

        private int _trimRate = 100;
        /// <summary>Stick-trim (#155): full-deflection adjust speed in
        /// percent of the trigger range per second.</summary>
        public int TrimRate
        {
            get => _trimRate;
            set => SetProperty(ref _trimRate, System.Math.Clamp(value, 1, 1000));
        }

        private bool _trimResetOnRelease = true;
        /// <summary>Stick-trim (#155): releasing the gate snaps the
        /// stored level back to 100% when true; false keeps it.</summary>
        public bool TrimResetOnRelease
        {
            get => _trimResetOnRelease;
            set => SetProperty(ref _trimResetOnRelease, value);
        }

        public RelayCommand ResetTrimDeadzoneCommand =>
            _resetTrimDeadzone ??= new RelayCommand(() => TrimDeadzone = 25);
        private RelayCommand _resetTrimDeadzone;

        public RelayCommand ResetTrimRateCommand =>
            _resetTrimRate ??= new RelayCommand(() => TrimRate = 100);
        private RelayCommand _resetTrimRate;

        public RelayCommand ResetTrimResetOnReleaseCommand =>
            _resetTrimResetOnRelease ??= new RelayCommand(() => TrimResetOnRelease = true);
        private RelayCommand _resetTrimResetOnRelease;

        /// <summary>Friendly entry for the Combine dropdown. Pairs the
        /// engine's mode name (Value, e.g. "MaxAbs") with a layman
        /// label and one-line description so non-STEM users aren't
        /// staring at "OR" / "XOR" / "MaxAbs" with no context.</summary>
        public sealed class CombineModeOption
        {
            public string Value { get; set; } = "";
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
        }

        // Cached once per culture so the WPF ComboBox binding doesn't
        // reallocate seven CombineModeOption objects + run fourteen
        // ResourceManager lookups every time it re-reads the property.
        // Without the cache, hovering / clicking inside the Mappings tab
        // burned visible CPU because virtualization + per-row binding
        // re-evaluation kept refetching this list.
        private static CombineModeOption[] _availableCombineModesCache;
        private static CombineModeOption[] _availableCombineModesNoTrimCache;
        private static int _availableCombineModesCacheCulture;

        /// <summary>Trigger-target rows see the full list; every other
        /// row gets the list without StickTrim (#155), the engine only
        /// intercepts that mode at the trigger sites.</summary>
        public System.Collections.Generic.IReadOnlyList<CombineModeOption> AvailableCombineModes
            => IsTriggerTarget
                ? GetAvailableCombineModes()
                : GetAvailableCombineModesWithoutTrim();

        // Keyed by reference identity of the full array it was derived
        // from: a culture change swaps the full cache first (inside
        // GetAvailableCombineModes), so a culture-stamp check here would
        // pass while still holding the old culture's strings.
        private static CombineModeOption[] _noTrimDerivedFrom;

        private static CombineModeOption[] GetAvailableCombineModesWithoutTrim()
        {
            var full = GetAvailableCombineModes();
            if (ReferenceEquals(_noTrimDerivedFrom, full) && _availableCombineModesNoTrimCache != null)
                return _availableCombineModesNoTrimCache;
            var filtered = System.Array.FindAll(full, o => o.Value != "StickTrim");
            _availableCombineModesNoTrimCache = filtered;
            _noTrimDerivedFrom = full;
            return filtered;
        }

        private static CombineModeOption[] GetAvailableCombineModes()
        {
            int currentCulture = System.Globalization.CultureInfo.CurrentUICulture.LCID;
            var cached = _availableCombineModesCache;
            if (cached != null && _availableCombineModesCacheCulture == currentCulture)
                return cached;

            var s = PadForge.Resources.Strings.Strings.Instance;
            var arr = new[]
            {
                new CombineModeOption { Value = "MaxAbs",  Name = s.Pad_Combine_MaxAbs_Name,  Description = s.Pad_Combine_MaxAbs_Description },
                new CombineModeOption { Value = "Sum",     Name = s.Pad_Combine_Sum_Name,     Description = s.Pad_Combine_Sum_Description },
                new CombineModeOption { Value = "Average", Name = s.Pad_Combine_Average_Name, Description = s.Pad_Combine_Average_Description },
                new CombineModeOption { Value = "OR",      Name = s.Pad_Combine_OR_Name,      Description = s.Pad_Combine_OR_Description },
                new CombineModeOption { Value = "AND",     Name = s.Pad_Combine_AND_Name,     Description = s.Pad_Combine_AND_Description },
                new CombineModeOption { Value = "XOR",     Name = s.Pad_Combine_XOR_Name,     Description = s.Pad_Combine_XOR_Description },
                new CombineModeOption { Value = "Custom",  Name = s.Pad_Combine_Custom_Name,  Description = s.Pad_Combine_Custom_Description },
                new CombineModeOption { Value = "StickTrim", Name = s.Pad_Combine_StickTrim_Name, Description = s.Pad_Combine_StickTrim_Description },
            };
            _availableCombineModesCache = arr;
            _availableCombineModesCacheCulture = currentCulture;
            return arr;
        }

        /// <summary>Live parse status of <see cref="CombineExpression"/>.
        /// "✓ valid" or a parse-error message; surfaced inline below
        /// the Custom expression TextBox so users get immediate
        /// feedback. Empty/whitespace expression compiles as 0 (always
        /// valid).</summary>
        public string CombineExpressionStatus
        {
            get
            {
                var s = PadForge.Resources.Strings.Strings.Instance;
                if (string.IsNullOrWhiteSpace(_combineExpression))
                    return s.Pad_Formula_Status_Empty;
                var c = Engine.Common.Mapping.MappingExpression.Compile(_combineExpression);
                if (!c.IsValid)
                    return "✗ " + (c.Error ?? s.Pad_Formula_Status_ParseError);

                var refs = c.ReferencedSingleLetterVars ?? "";
                // Inline the friendly alias for each referenced letter
                // so users see what the variable means here, e.g.
                // "refs: a (DualSense · A), b (Keyboard · W)".
                string refsBit = "";
                if (!string.IsNullOrEmpty(refs))
                {
                    var parts = new System.Collections.Generic.List<string>(refs.Length);
                    foreach (char letter in refs)
                    {
                        string alias = GetVariableAlias(letter - 'a');
                        parts.Add(string.IsNullOrEmpty(alias) ? letter.ToString() : letter + " (" + alias + ")");
                    }
                    refsBit = " · " + s.Pad_Formula_Status_RefsLabel + ": " + string.Join(", ", parts);
                }
                if (c.MaxIndexedRef >= 0)
                    refsBit += (refsBit.Length == 0 ? " · " + s.Pad_Formula_Status_RefsLabel + ": " : ", ") + "s[" + c.MaxIndexedRef + "]";

                // Effective source count: exactly the slots the engine
                // builds, which is NOT primary + ExtraSources.Count once a
                // Neg pair or an InvertOnHold modifier is on the row.
                int sourceCount = PositionalSourceCount;

                var outOfRange = new System.Collections.Generic.List<char>();
                foreach (char letter in refs)
                {
                    int idx = letter - 'a';
                    if (idx >= sourceCount) outOfRange.Add(letter);
                }
                bool indexedOutOfRange = c.MaxIndexedRef >= sourceCount;

                if (outOfRange.Count == 0 && !indexedOutOfRange)
                    return s.Pad_Formula_Status_Valid + refsBit;

                // Warn when the formula reaches past the row's actual
                // sources. The engine returns 0 for missing variables
                // so the formula doesn't crash, but the user almost
                // certainly didn't mean for that source to silently
                // be a constant 0.
                string warn;
                if (outOfRange.Count > 0 && indexedOutOfRange)
                    warn = string.Join(",", outOfRange) + " " + s.Pad_Formula_Status_And + " s[" + c.MaxIndexedRef + "] " + s.Pad_Formula_Status_NoSourcePlural;
                else if (outOfRange.Count > 0)
                    warn = (outOfRange.Count == 1
                            ? outOfRange[0] + " " + s.Pad_Formula_Status_NoSourceSingular
                            : string.Join(",", outOfRange) + " " + s.Pad_Formula_Status_NoSourcePlural);
                else
                    warn = "s[" + c.MaxIndexedRef + "] " + s.Pad_Formula_Status_NoSourceSingular;
                return "⚠ " + s.Pad_Formula_Status_Valid.TrimStart('✓', ' ') + refsBit + " — " + warn + " (" + s.Pad_Formula_Status_TreatedAsZero + ")";
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

        // ─────────────────────────────────────────────
        //  Variable alias labels (option A from the design note)
        //
        //  The formula text stays positional (a, b, c…). For display
        //  only, we surface friendly labels for each variable so chip
        //  tooltips and the status line tell the user what `a / b / c`
        //  currently mean on THIS row. Engine semantics unchanged —
        //  no parser changes, no storage changes, no rename
        //  brittleness.
        // ─────────────────────────────────────────────

        public string VariableALabel => GetVariableAlias(0);
        public string VariableBLabel => GetVariableAlias(1);
        public string VariableCLabel => GetVariableAlias(2);
        public string VariableDLabel => GetVariableAlias(3);

        /// <summary>Composite tooltip strings for the formula chips.
        /// Show the chip's positional meaning plus the friendly alias
        /// for the row's current source at that position.</summary>
        public string VariableATooltip => BuildVariableTooltip(PadForge.Resources.Strings.Strings.Instance.Pad_Formula_Var_PositionA, VariableALabel);
        public string VariableBTooltip => BuildVariableTooltip(PadForge.Resources.Strings.Strings.Instance.Pad_Formula_Var_PositionB, VariableBLabel);
        public string VariableCTooltip => BuildVariableTooltip(PadForge.Resources.Strings.Strings.Instance.Pad_Formula_Var_PositionC, VariableCLabel);
        public string VariableDTooltip => BuildVariableTooltip(PadForge.Resources.Strings.Strings.Instance.Pad_Formula_Var_PositionD, VariableDLabel);

        private static string BuildVariableTooltip(string positionLabel, string alias)
            => string.IsNullOrEmpty(alias)
                ? positionLabel + " — " + PadForge.Resources.Strings.Strings.Instance.Pad_Formula_Var_NotYetMapped
                : positionLabel + " · " + alias;

        /// <summary>The sources that occupy a Custom formula's positional
        /// slots, in order, mirroring what the engine's contribution builder
        /// does. Two classes are skipped there with NO placeholder, so they
        /// shift every later letter:
        ///
        /// <para>the bipolar Neg pair, which is Sources[1] carrying the same
        /// device as the primary with Invert flipped on a bipolar-axis
        /// target, because it merges into the primary's own slot; and any
        /// InvertOnHold source, which is a row modifier and never enters the
        /// combine. Null and postpone-suppressed sources DO get a 0f
        /// placeholder there, precisely to keep the letters stable, so they
        /// still count here.</para>
        ///
        /// <para>The UI used to count the primary plus ExtraSources.Count
        /// flat. Since the load path materializes Sources[1..] as visible
        /// ExtraSources rows, including the promoted Neg pair, that count
        /// ran ahead of the engine's: a formula referencing the last letter
        /// validated green in the editor and silently read nothing at
        /// runtime, and the chip aliases named the wrong physical input.
        /// Entries are null for the primary slot.</para></summary>
        internal System.Collections.Generic.List<MappingSourceItem> PositionalSources()
        {
            var slots = new System.Collections.Generic.List<MappingSourceItem>(4);
            bool primaryIsModifier = string.Equals(
                PrimaryKindSource?.Kind ?? "Direct", "InvertOnHold", StringComparison.Ordinal);
            bool primaryExists = !string.IsNullOrEmpty(_sourceDescriptor) || primaryIsModifier;
            if (primaryExists && !primaryIsModifier) slots.Add(null);

            if (ExtraSources == null) return slots;

            // The engine's neg-pair test, on the same two facts: Sources[1]
            // shares the primary's device and its Invert is flipped.
            int negPairIdx = -1;
            if (IsBipolarAxisTarget && primaryExists && ExtraSources.Count > 0)
            {
                PadForge.Engine.Common.Mapping.SourceCoercion.StripLegacyPrefix(
                    _sourceDescriptor, out bool primaryInvert, out _);
                var first = ExtraSources[0];
                if (first != null
                    && string.Equals(first.DeviceGuid ?? "", PrimarySourceDeviceGuid ?? "",
                        StringComparison.OrdinalIgnoreCase)
                    && first.Invert != primaryInvert)
                    negPairIdx = 0;
            }

            for (int i = 0; i < ExtraSources.Count; i++)
            {
                if (i == negPairIdx) continue;
                var extra = ExtraSources[i];
                if (extra != null && string.Equals(extra.Kind ?? "Direct", "InvertOnHold",
                        StringComparison.Ordinal))
                    continue;
                slots.Add(extra);
            }
            return slots;
        }

        /// <summary>How many letters a Custom formula may reference on this
        /// row. See <see cref="PositionalSources"/> for why this is not
        /// simply the primary plus ExtraSources.Count.</summary>
        internal int PositionalSourceCount => PositionalSources().Count;

        /// <summary>Returns "DeviceLabel · InputName" for the source
        /// at the given position (0 = primary, 1+ = ExtraSources in
        /// UI order). Empty if no source occupies that slot.</summary>
        private string GetVariableAlias(int index)
        {
            if (index == 0)
            {
                if (string.IsNullOrEmpty(_sourceDescriptor)) return "";
                string name = _selectedInput?.DisplayName ?? _resolvedSourceText ?? _sourceDescriptor;
                return string.IsNullOrEmpty(_primarySourceDeviceLabel)
                    ? name : _primarySourceDeviceLabel + " · " + name;
            }
            var slots = PositionalSources();
            if (index < 0 || index >= slots.Count) return "";
            var extra = slots[index];
            if (extra == null) return "";
            if (extra == null || string.IsNullOrEmpty(extra.Descriptor)) return "";
            string ename = extra.SelectedInput?.DisplayName ?? extra.Descriptor;
            return string.IsNullOrEmpty(extra.DeviceLabel)
                ? ename : extra.DeviceLabel + " · " + ename;
        }

        private void RefreshVariableAliases()
        {
            OnPropertyChanged(nameof(VariableALabel));
            OnPropertyChanged(nameof(VariableBLabel));
            OnPropertyChanged(nameof(VariableCLabel));
            OnPropertyChanged(nameof(VariableDLabel));
            OnPropertyChanged(nameof(VariableATooltip));
            OnPropertyChanged(nameof(VariableBTooltip));
            OnPropertyChanged(nameof(VariableCTooltip));
            OnPropertyChanged(nameof(VariableDTooltip));
            OnPropertyChanged(nameof(CombineExpressionStatus));
        }

        /// <summary>True when the expression is valid but references
        /// variables beyond the row's actual source count — those
        /// silently evaluate to 0 in the engine, which is rarely what
        /// the user intended.</summary>
        public bool IsCombineExpressionWarning
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_combineExpression)) return false;
                var c = Engine.Common.Mapping.MappingExpression.Compile(_combineExpression);
                if (!c.IsValid) return false;
                int sourceCount = PositionalSourceCount;
                foreach (char letter in c.ReferencedSingleLetterVars ?? "")
                {
                    int idx = letter - 'a';
                    if (idx >= sourceCount) return true;
                }
                return c.MaxIndexedRef >= sourceCount;
            }
        }

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
                || t.StartsWith("RawAxis", StringComparison.Ordinal)
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
                else if (clean.StartsWith("I", StringComparison.OrdinalIgnoreCase) && clean.Length > 1 && !char.IsDigit(clean[1])
                         && !PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(clean))
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
        Touchpad,
        // Bundled motion-passthrough rows: MotionGyro / MotionAccel
        // targets bound to a device's Motion Gyro / Motion Accel sources.
        // Auto-created on Sony-class VC slots for every assigned gyro /
        // accel-capable device; rendered as a single combined Motion row
        // in the UI when both sub-channels come from the same source.
        Motion
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
