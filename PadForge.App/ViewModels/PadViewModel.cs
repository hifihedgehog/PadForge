using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;

namespace PadForge.ViewModels
{
    /// <summary>Localized display + canonical-value pair for combo-box
    /// option lists where the stored value must stay locale-stable
    /// (e.g. gyro space "Local"/"Player"/"World", output curve names,
    /// units selector). Display is a lazy lookup against
    /// <c>Strings.Instance</c> so the visible text follows the current
    /// UI culture without rebuilding the list.</summary>
    public sealed class GyroLabeledOption
    {
        private readonly Func<string> _displayLookup;
        public GyroLabeledOption(Func<string> displayLookup, string value)
        {
            _displayLookup = displayLookup;
            Value = value;
        }
        public string Display => _displayLookup?.Invoke() ?? Value;
        public string Value { get; }
        public override string ToString() => Display;
    }

    /// <summary>
    /// ViewModel for a single virtual controller slot (one of 16 pads).
    /// Features:
    ///   #1 — Multi-device selection: SelectedMappedDevice picks which device to configure
    ///   #2 — Expanded deadzones: per-axis X/Y, trigger deadzones, anti-deadzone, linear
    ///   #4 — Macro foundation: trigger combos → action sequences
    /// </summary>
    public partial class PadViewModel : ViewModelBase
    {
        public PadViewModel(int padIndex)
        {
            PadIndex = padIndex;
            _slotNumber = padIndex + 1;
            Title = string.Format(Strings.Instance.Main_VirtualController_Format, padIndex + 1);
            SlotLabel = string.Format(Strings.Instance.Main_VirtualController_Format, padIndex + 1);
            _extendedConfig.PropertyChanged += OnExtendedConfigPropertyChanged;
            // AvailableProfiles is a computed property that reads from
            // HMaestroProfileCatalog. When the catalog reloads (e.g. after
            // a user import), raise PropertyChanged so the dropdown's
            // ItemsSource binding picks up the new entries.
            HMaestroProfileCatalog.CatalogReloaded += OnCatalogReloaded;
            // #175 telemetry board: keep the "{n} DIRECT" readout in sync
            // with row edits and rebuilds. Hooked before the first
            // RebuildMappings so the initial rows are counted too.
            Mappings.CollectionChanged += OnMappingsChangedForDirectCount;
            // Bass Shakers rows (#236): the four fixed voice items exist
            // for the slot's lifetime. Built before RebuildMappings because
            // its ReloadRumbleAudio leg re-seeds them from the slot set.
            for (int i = 0; i < RumbleAudioConfig.SourceOrder.Length; i++)
                RumbleAudioVoices.Add(new RumbleAudioVoiceItem(this, i));
            RebuildMappings();
            RebuildStickConfigs();
            RebuildTriggerConfigs();
            // Seed the layer tab strip with just Base so the binding has
            // a non-null collection from construction time. InputService
            // calls RebuildLayerTabs with the slot's actual activators
            // during ApplyProfile and after any add / edit / delete.
            RebuildLayerTabs(null);
        }

        private void OnCatalogReloaded(object sender, EventArgs e)
        {
            OnPropertyChanged(nameof(AvailableProfiles));
        }

        protected override void OnCultureChanged()
        {
            Title = string.Format(Strings.Instance.Main_VirtualController_Format, PadIndex + 1);
            SlotLabel = string.Format(Strings.Instance.Main_VirtualController_Format, PadIndex + 1);

            // Rebuild mappings so target labels (Back, Start, etc.) and source display
            // text are re-evaluated in the new language.
            RebuildMappings();
            RebuildStickConfigs();
            RebuildTriggerConfigs();

            // Map All button text + tooltip are computed-on-demand off
            // Strings.Instance.*. Without an explicit notification the
            // bindings keep the old language's value until the next
            // IsMapAllActive flip.
            OnPropertyChanged(nameof(MapAllButtonText));
            OnPropertyChanged(nameof(MapAllButtonTooltip));
            OnPropertyChanged(nameof(OutputTypeDisplayName));

            // Pipeline chip summaries carry localized preset names, so
            // the memo and the cached config both re-localize.
            _matchPresetCache.Clear();
            _pipelineChipsConfigDirty = true;
        }

        /// <summary>Zero-based pad slot index (0–15).</summary>
        public int PadIndex { get; }

        // Shift activator UI lands in the Phase 6 commit chain (v1+).
        // ShiftActivators authored in PadPage.Mappings drive
        // MappingSet.ShiftActivators; the engine's
        // InputManager.Step3.MappingSetEval.ResolveActiveLayerMask reads
        // that list and resolves the active layer mask via last-engaged-
        // wins. Empty ShiftActivators list = no shift layers; the engine
        // returns "Base" and the pipeline behaves as single-layer.

        /// <summary>
        /// Callback invoked when a config item property changes that needs to be persisted.
        /// Wired by MainWindow to call SettingsService.MarkDirty().
        /// </summary>
        public Action ConfigItemDirtyCallback { get; set; }

        /// <summary>Wired by MainWindow to re-stamp the engine's per-slot MappingSets
        /// immediately on a steering-mode change (including Reset all -> Direct), so the
        /// stick stops/starts steering at once instead of after the 2s autosave.</summary>
        public Action SteeringModeChangedCallback { get; set; }

        private int _slotNumber;
        /// <summary>One-based sequential number among active slots, for display.</summary>
        public int SlotNumber
        {
            get => _slotNumber;
            set => SetProperty(ref _slotNumber, value);
        }

        private string _slotLabel;
        /// <summary>Display label (e.g., "Virtual Controller 1").</summary>
        public string SlotLabel
        {
            get => _slotLabel;
            set => SetProperty(ref _slotLabel, value);
        }

        // ═══════════════════════════════════════════════
        //  Output type (Xbox / PlayStation)
        // ═══════════════════════════════════════════════

        private VirtualControllerType _outputType;

        /// <summary>Virtual controller output type for this slot.</summary>
        public VirtualControllerType OutputType
        {
            get => _outputType;
            set
            {
                if (SetProperty(ref _outputType, value))
                {
                    // Raise AvailableProfiles FIRST so the dropdown's
                    // ItemsSource refreshes to the new category's profile
                    // list BEFORE the ProfileId assignment below triggers
                    // the SelectedValue binding. With the old order (ProfileId
                    // first), SelectedValue resolved against the stale
                    // previous-category ItemsSource, failed to match, and
                    // left the dropdown visually blank even though the
                    // backend was running with the correct default profile.
                    OnPropertyChanged(nameof(AvailableProfiles));
                    OnPropertyChanged(nameof(HasHMaestroProfileBar));
                    OnPropertyChanged(nameof(OutputTypeDisplayName));
                    // Bass Shakers tab (#236): slot-type gate follows the
                    // output type, never a physical-device capability.
                    OnPropertyChanged(nameof(RumbleAudioTabVisible));

                    // Category change invalidates the previous HIDMaestro
                    // profile slug. Assign the new category's default so the
                    // dropdown lands on a valid selection immediately. The
                    // engine-side fallback still catches null, but the UI
                    // binds to ProfileId directly.
                    ProfileId = PadForge.Common.Input.InputManager.GetDefaultProfileId(value);
                    ResetDeadZoneSettings();
                    RebuildMappings();
                    RebuildStickConfigs();
                    RebuildTriggerConfigs();
                    SyncMacroButtonStyle();
                }
            }
        }

        private string _profileId;
        /// <summary>
        /// HIDMaestro profile slug for this slot (e.g. "xbox-360-wired",
        /// "dualsense", "logitech-g920"). Null/empty falls back to the
        /// active category's default profile in CreateVirtualController.
        /// </summary>
        public string ProfileId
        {
            get => _profileId;
            set
            {
                if (SetProperty(ref _profileId, value))
                {
                    // For Extended slots, the profile defines the VC's layout.
                    // Sync ExtendedConfig's stick/trigger/POV/button counts from
                    // the newly-selected profile's HID descriptor metadata,
                    // then rebuild the mapping grid + stick/trigger configs
                    // so the UI reflects the profile's actual axes/buttons.
                    // Xbox / PlayStation slots have fixed layouts that don't
                    // vary per profile, so no rebuild is needed there.
                    if (_outputType == VirtualControllerType.Extended)
                    {
                        SyncExtendedConfigFromProfile();
                        // Force Customize on whenever the user picks the
                        // synthetic "Custom" entry — it's only useful as a
                        // customization target, so making the user toggle
                        // Customize separately would be UX friction. Other
                        // profiles keep Customize in its current state.
                        if (string.Equals(value, HMaestroProfileCatalog.CustomProfileId, System.StringComparison.Ordinal))
                            _extendedConfig.Customize = true;
                        RebuildMappings();
                        RebuildStickConfigs();
                        RebuildTriggerConfigs();
                        // Macro / menu button lettering follows the profile
                        // on Extended slots (#215, Switch Pro letters).
                        SyncMacroButtonStyle();
                        // Bass Shakers gate (#236): an uncustomized slot
                        // follows the newly-picked profile's descriptor.
                        RefreshRumbleAudioTabGate();
                    }
                    else if (_outputType == VirtualControllerType.Xbox)
                    {
                        // Xbox Series profiles add a Share row that other
                        // Xbox profiles (360 / One / Wireless) don't expose,
                        // so the Mappings list must rebuild when the profile
                        // selection changes (xbox-series-* ↔ anything else).
                        RebuildMappings();
                    }
                    ConfigItemDirtyCallback?.Invoke();
                }
            }
        }

        /// <summary>
        /// Populates ExtendedConfig's layout counts from the active HIDMaestro
        /// profile so the dynamic Extended mapping grid auto-sizes to match
        /// the profile's actual HID descriptor. Counts come from
        /// HMProfile's v1.3.9 simple-view properties (StickCount /
        /// TriggerCount) which derive from the profile's Layout block
        /// when authored, falling back to the descriptor-shape classifier
        /// otherwise. POVs come from HasHat; buttons from ButtonCount.
        ///
        /// <para>The earlier <c>AxisCount / 2</c> formula assumed every
        /// profile fit the canonical "two sticks then trailing triggers"
        /// gamepad convention. That breaks on wheels (Logitech G25 reports
        /// 4 axes that are actually 1 stick + 2 triggers, not 2 + 0),
        /// flight sticks (1 stick + throttle, not 0.5 stick + ...), and
        /// every other non-gamepad shape. v1.3.9's StickCount/TriggerCount
        /// account for the descriptor's role tags (HMSimpleStick is paired
        /// X+Y, HMSimpleTrigger is single-axis) so the row count finally
        /// matches the physical device.</para>
        ///
        /// <para>Sets Preset to Custom so downstream isExtended checks route
        /// through the dynamic mapping path rather than the fixed Xbox layout.</para>
        /// </summary>
        private void SyncExtendedConfigFromProfile()
        {
            var profile = AvailableProfiles?.FirstOrDefault(p =>
                string.Equals(p.Id, _profileId, System.StringComparison.OrdinalIgnoreCase));
            if (profile == null) return;

            _extendedConfig.ThumbstickCount = profile.StickCount;
            _extendedConfig.TriggerCount = profile.TriggerCount;
            _extendedConfig.PovCount = profile.HasHat ? 1 : 0;
            _extendedConfig.ButtonCount = profile.ButtonCount;
        }

        /// <summary>
        /// HIDMaestro profile list for the current category, filtered for the
        /// PadPage profile picker dropdown. Empty for MIDI / KeyboardMouse
        /// slots which don't use HIDMaestro.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<HIDMaestro.HMProfile> AvailableProfiles =>
            _outputType switch
            {
                VirtualControllerType.Xbox => HMaestroProfileCatalog.XboxProfiles,
                VirtualControllerType.PlayStation => HMaestroProfileCatalog.PlayStationProfiles,
                VirtualControllerType.Extended => HMaestroProfileCatalog.ExtendedProfiles,
                _ => System.Array.Empty<HIDMaestro.HMProfile>()
            };

        /// <summary>
        /// True when the slot's category uses an HIDMaestro profile (Xbox /
        /// PlayStation / Extended). Drives the visibility of the profile picker bar
        /// on the PadPage.
        /// </summary>
        public bool HasHMaestroProfileBar =>
            _outputType == VirtualControllerType.Xbox
            || _outputType == VirtualControllerType.PlayStation
            || _outputType == VirtualControllerType.Extended;

        private string _typeInstanceLabel = "1";
        /// <summary>Per-type instance number label (e.g., "1", "2"). Set by RefreshNavControllerItems.</summary>
        public string TypeInstanceLabel
        {
            get => _typeInstanceLabel;
            set => SetProperty(ref _typeInstanceLabel, value);
        }

        /// <summary>Localized virtual-controller type name (Xbox / PlayStation /
        /// Extended / Keyboard+Mouse / MIDI). Consolidated from the per-type
        /// Strings so the tier-1 header can render it as one Run beside the mono
        /// instance token on a shared text baseline (the two used to be separate
        /// TextBlocks, and the mono #N sat low). Re-raised on OutputType and
        /// culture changes.</summary>
        public string OutputTypeDisplayName => _outputType switch
        {
            VirtualControllerType.Xbox => Strings.Instance.ControllerType_Xbox,
            VirtualControllerType.PlayStation => Strings.Instance.ControllerType_PlayStation,
            VirtualControllerType.Extended => Strings.Instance.ControllerType_Extended,
            VirtualControllerType.KeyboardMouse => Strings.Instance.ControllerType_KeyboardMouse,
            VirtualControllerType.Midi => Strings.Instance.ControllerType_MIDI,
            _ => string.Empty
        };

        /// <summary>Int binding for ComboBox SelectedIndex (0=Xbox, 1=PlayStation).</summary>
        public int OutputTypeIndex
        {
            get => (int)_outputType;
            set
            {
                if (Enum.IsDefined(typeof(VirtualControllerType), value))
                    OutputType = (VirtualControllerType)value;
            }
        }

        // ═══════════════════════════════════════════════
        //  Extended per-slot configuration
        // ═══════════════════════════════════════════════

        private ExtendedSlotConfig _extendedConfig = new();

        /// <summary>
        /// Per-slot Extended configuration (preset, axis/button counts).
        /// Always present — only meaningful when OutputType == Extended.
        /// </summary>
        public ExtendedSlotConfig ExtendedConfig
        {
            get => _extendedConfig;
            set
            {
                if (_extendedConfig != null)
                    _extendedConfig.PropertyChanged -= OnExtendedConfigPropertyChanged;
                if (SetProperty(ref _extendedConfig, value) && value != null)
                    value.PropertyChanged += OnExtendedConfigPropertyChanged;
            }
        }

        private void OnExtendedConfigPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // When Extended config changes (counts), rebuild dynamic collections
            if (OutputType == VirtualControllerType.Extended)
            {
                switch (e.PropertyName)
                {
                    case nameof(ExtendedSlotConfig.ThumbstickCount):
                    case nameof(ExtendedSlotConfig.TriggerCount):
                        ResetDeadZoneSettings();
                        RebuildMappings();
                        RebuildStickConfigs();
                        RebuildTriggerConfigs();
                        break;
                    case nameof(ExtendedSlotConfig.PovCount):
                        RebuildMappings();
                        break;
                    case nameof(ExtendedSlotConfig.ButtonCount):
                        RebuildMappings();
                        SyncMacroButtonStyle();
                        break;
                    case nameof(ExtendedSlotConfig.ForceFeedbackEnabled):
                    case nameof(ExtendedSlotConfig.Customize):
                        // Bass Shakers rides the PID force feedback lane
                        // (#236): the tab follows the effective FFB state.
                        RefreshRumbleAudioTabGate();
                        break;
                }
            }
        }

        // ═══════════════════════════════════════════════
        //  PlayStation per-slot configuration
        //  Drives the Adaptive Triggers and Lighting tabs.
        //  Meaningful only when OutputType == PlayStation AND the
        //  active profile has the relevant capability (DualSense /
        //  DualSense Edge for adaptive triggers; DS4 / DualSense /
        //  DualSense Edge for lighting). Tab visibility gates the
        //  UI surface; the storage instance is always present so XML
        //  round-trip and profile-switch flows stay symmetrical with
        //  ExtendedConfig.
        // ═══════════════════════════════════════════════

        // Per-(slot, device) lighting tab configs. The Lighting tab is
        // per-device (parallel to PadSetting): different physical devices
        // mapped to the same slot can have different lightbar mode,
        // colors, palette, audio response, etc. Macro lightbar actions
        // fan out across every entry here so multiple DualSenses on one
        // slot move together while each renders its own personality
        // (palette, colors) for the new mode.
        //
        // <c>_deviceConfig</c> below is a reference to the
        // SelectedMappedDevice's entry in this dictionary, swapped on
        // device change so the Lighting tab's bindings re-resolve.
        // ConcurrentDictionary so the polling thread (which iterates this
        // every dispatch tick + every macro lightbar action) doesn't race
        // with UI-thread mutations from device-map changes / settings
        // load. Iteration on ConcurrentDictionary returns a moment-in-time
        // snapshot rather than throwing InvalidOperationException.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DeviceSlotConfig> _perDeviceSlotConfigs = new();
        private DeviceSlotConfig _deviceConfig = new();
        private static readonly DeviceSlotConfig _emptyDeviceConfigSentinel = new();

        /// <summary>Per-(slot, device) lighting tab configs keyed by
        /// physical device InstanceGuid. Always populated for every
        /// mapped device; the empty Guid key holds a fallback used
        /// before any device is mapped.</summary>
        public IReadOnlyDictionary<Guid, DeviceSlotConfig> PerDeviceSlotConfigs
            => _perDeviceSlotConfigs;

        /// <summary>The lighting tab's currently-bound config —
        /// references the SelectedMappedDevice's entry in
        /// <see cref="PerDeviceSlotConfigs"/>. The setter accepts
        /// any config (used by load paths to seed the dictionary
        /// before SelectedMappedDevice is set). On swap, the forwarder
        /// re-attaches its PropertyChanged subscription so listeners
        /// of <see cref="ActiveDeviceConfigPropertyChanged"/>
        /// keep receiving events from whichever per-device config is
        /// currently bound.</summary>
        public DeviceSlotConfig DeviceConfig
        {
            get => _deviceConfig;
            set
            {
                var old = _deviceConfig;
                if (SetProperty(ref _deviceConfig, value ?? new()))
                {
                    if (old != null) old.PropertyChanged -= OnActiveDeviceConfigPropertyChanged;
                    if (_deviceConfig != null) _deviceConfig.PropertyChanged += OnActiveDeviceConfigPropertyChanged;
                }
            }
        }

        /// <summary>Forwards PropertyChanged from the currently-bound
        /// <see cref="DeviceConfig"/> regardless of which
        /// per-device entry the anchor points at. Subscribers attach to
        /// this rather than to the inner config so the subscription
        /// follows the anchor across SelectedMappedDevice swaps.</summary>
        public event System.ComponentModel.PropertyChangedEventHandler ActiveDeviceConfigPropertyChanged;

        private void OnActiveDeviceConfigPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
            => ActiveDeviceConfigPropertyChanged?.Invoke(sender, e);

        /// <summary>Returns the per-device lighting config for the given
        /// device, creating a fresh default entry if none exists yet.
        /// Used by macro fan-out and the polling-thread synthesizer to
        /// resolve a specific (slot, device) config.</summary>
        public DeviceSlotConfig GetOrCreateDeviceConfig(Guid deviceGuid)
        {
            if (deviceGuid == Guid.Empty)
                return _deviceConfig;
            return _perDeviceSlotConfigs.GetOrAdd(deviceGuid, _ => new DeviceSlotConfig());
        }

        /// <summary>Snapshot of every per-device config on the slot. Used
        /// by macro fan-out so a slot-level lightbar action writes to
        /// each device's config in turn.</summary>
        public IEnumerable<DeviceSlotConfig> EnumerateDeviceSlotConfigs()
            => _perDeviceSlotConfigs.Values;

        /// <summary>Ensures the per-device dictionary has an entry for
        /// every currently-mapped device. Newly-mapped devices get a
        /// fresh default config — the user customizes each device's
        /// Lighting tab independently. Called by the input service
        /// after MappedDevices changes.</summary>
        public void EnsureDeviceSlotConfigsForMappedDevices()
        {
            foreach (var dev in MappedDevices)
            {
                if (dev.InstanceGuid == Guid.Empty) continue;
                _perDeviceSlotConfigs.GetOrAdd(dev.InstanceGuid, _ => new DeviceSlotConfig());
            }
        }

        /// <summary>Switches the Lighting tab's bound config to the
        /// device with the given InstanceGuid, creating an entry if
        /// missing. Called by SelectedMappedDevice change.</summary>
        private void BindDeviceConfigForDevice(Guid deviceGuid)
        {
            DeviceSlotConfig target;
            if (deviceGuid == Guid.Empty)
            {
                // No device selected — bind a sentinel so the UI doesn't
                // mutate any device's config inadvertently.
                target = _emptyDeviceConfigSentinel;
            }
            else
            {
                target = GetOrCreateDeviceConfig(deviceGuid);
            }
            if (!ReferenceEquals(_deviceConfig, target))
                DeviceConfig = target;
        }

        // ═══════════════════════════════════════════════
        //  MIDI per-slot configuration
        // ═══════════════════════════════════════════════

        private MidiSlotConfig _midiConfig = new();

        /// <summary>
        /// Per-slot MIDI configuration (port, channel, CC/note mappings).
        /// Always present — only meaningful when OutputType == Midi.
        /// </summary>
        public MidiSlotConfig MidiConfig
        {
            get => _midiConfig;
            set => SetProperty(ref _midiConfig, value ?? new());
        }

        // ═══════════════════════════════════════════════
        //  KBM per-slot configuration (discussion #205)
        // ═══════════════════════════════════════════════

        private KbmSlotConfig _kbmConfig = new();

        /// <summary>
        /// Per-slot keyboard + mouse configuration (SOCD / Snap Tap).
        /// Always present. Only meaningful when OutputType == KeyboardMouse.
        /// </summary>
        public KbmSlotConfig KbmConfig
        {
            get => _kbmConfig;
            set => SetProperty(ref _kbmConfig, value ?? new());
        }

        // ═══════════════════════════════════════════════
        //  #1: Multi-device selection within a slot
        // ═══════════════════════════════════════════════

        /// <summary>
        /// Info about a single physical device mapped to this virtual controller slot.
        /// </summary>
        public class MappedDeviceInfo : ObservableObject
        {
            private string _name = "Unknown";
            private Guid _instanceGuid;
            private bool _isOnline;

            private string _batteryGlyph = string.Empty;

            /// <summary>Segoe MDL2 battery glyph for the crucible card roster
            /// (#175). Same bucketing as DeviceRowViewModel.BatteryGlyph.</summary>
            public string BatteryGlyph
            {
                get => _batteryGlyph;
                set
                {
                    if (SetProperty(ref _batteryGlyph, value ?? string.Empty))
                        OnPropertyChanged(nameof(HasBattery));
                }
            }

            public bool HasBattery => !string.IsNullOrEmpty(_batteryGlyph);

            private string _transportGlyph = string.Empty;

            /// <summary>Segoe MDL2 transport glyph for the crucible card
            /// roster (#175 competitor item 9): Bluetooth links carry E702,
            /// wired stays unmarked (the quiet default).</summary>
            public string TransportGlyph
            {
                get => _transportGlyph;
                set
                {
                    if (SetProperty(ref _transportGlyph, value ?? string.Empty))
                        OnPropertyChanged(nameof(HasTransportGlyph));
                }
            }

            public bool HasTransportGlyph => !string.IsNullOrEmpty(_transportGlyph);

            private string _typeGlyph = "\uE7FC";

            /// <summary>Segoe MDL2 device-class glyph for the crucible card
            /// roster (#175): keyboards read as keyboards, mice as mice.
            /// Sourced from DeviceTypeGlyph.For. Defaults to the controller
            /// glyph until InputService resolves the device.</summary>
            public string TypeGlyph
            {
                get => _typeGlyph;
                set => SetProperty(ref _typeGlyph, string.IsNullOrEmpty(value) ? "\uE7FC" : value);
            }

            public string Name
            {
                get => _name;
                set => SetProperty(ref _name, value);
            }

            public Guid InstanceGuid
            {
                get => _instanceGuid;
                set => SetProperty(ref _instanceGuid, value);
            }

            /// <summary>Per-device connect state, the same ud.IsOnline truth
            /// the card's "N connected" count reads (#175 phase 2 item 14).
            /// Refreshed every dashboard tick in RefreshSlotSummaryProperties
            /// from the same device snapshot as that count, so an offline
            /// roster line dims instead of rendering full-brightness cold.</summary>
            public bool IsOnline
            {
                get => _isOnline;
                set => SetProperty(ref _isOnline, value);
            }

            private string _batteryText = string.Empty;

            /// <summary>"78%" while the device reports a battery, empty
            /// otherwise (issue #167). Refreshed on the UI timer's slow lane.</summary>
            public string BatteryText
            {
                get => _batteryText;
                set => SetProperty(ref _batteryText, value ?? string.Empty);
            }

            public override string ToString() => Name;
        }

        /// <summary>All physical devices currently mapped to this slot.</summary>
        public ObservableCollection<MappedDeviceInfo> MappedDevices { get; } = new();

        /// <summary>Slot-level cross-device InputChoice list. Mirrors the
        /// per-MappingItem AvailableInputs but exposed at the slot level
        /// so the Gyro tab's Aim Engage picker (and any future slot-wide
        /// pickers) can bind without proxy-walking the Mappings list.
        /// Populated by <c>InputService.PopulateAvailableInputs</c>.</summary>
        public ObservableCollection<InputChoice> SlotAvailableInputs { get; } = new();

        private ICollectionView _slotAvailableInputsView;
        /// <summary>Grouped CollectionView over <see cref="SlotAvailableInputs"/>
        /// keyed on <c>DeviceLabel</c> for the picker's GroupStyle header.</summary>
        public ICollectionView SlotAvailableInputsView
        {
            get
            {
                if (_slotAvailableInputsView == null)
                {
                    _slotAvailableInputsView = CollectionViewSource.GetDefaultView(SlotAvailableInputs);
                    if (_slotAvailableInputsView.GroupDescriptions != null
                        && _slotAvailableInputsView.GroupDescriptions.Count == 0)
                    {
                        _slotAvailableInputsView.GroupDescriptions.Add(
                            new PropertyGroupDescription(nameof(InputChoice.DeviceLabel)));
                    }
                }
                return _slotAvailableInputsView;
            }
        }

        /// <summary>Slot-level InputChoice list for the macro trigger
        /// dropdown (#177): the subset of <see cref="SlotAvailableInputs"/>
        /// that converts to a <c>MacroItem.TriggerInputEntry</c> (raw
        /// buttons, POV directions, gamepad-layout axes, touchpad click,
        /// and touchpad gestures). Populated beside SlotAvailableInputs
        /// by <c>InputService.PopulateAvailableInputs</c>, so the same
        /// Touchpad-tab enable gates that govern the mapping picker
        /// govern what macros can trigger on.</summary>
        public ObservableCollection<InputChoice> SlotMacroTriggerChoices { get; } = new();

        private ICollectionView _slotMacroTriggerChoicesView;
        /// <summary>Grouped CollectionView over
        /// <see cref="SlotMacroTriggerChoices"/> keyed on
        /// <c>DeviceLabel</c>, matching the other slot-level pickers.</summary>
        public ICollectionView SlotMacroTriggerChoicesView
        {
            get
            {
                if (_slotMacroTriggerChoicesView == null)
                {
                    _slotMacroTriggerChoicesView = CollectionViewSource.GetDefaultView(SlotMacroTriggerChoices);
                    if (_slotMacroTriggerChoicesView.GroupDescriptions != null
                        && _slotMacroTriggerChoicesView.GroupDescriptions.Count == 0)
                    {
                        _slotMacroTriggerChoicesView.GroupDescriptions.Add(
                            new PropertyGroupDescription(nameof(InputChoice.DeviceLabel)));
                    }
                }
                return _slotMacroTriggerChoicesView;
            }
        }

        private MappedDeviceInfo _selectedMappedDevice;

        /// <summary>
        /// The currently selected device within this slot for configuration.
        /// When changed, the mapping grid and deadzone settings should update
        /// to reflect THIS device's PadSetting.
        /// </summary>
        public MappedDeviceInfo SelectedMappedDevice
        {
            get => _selectedMappedDevice;
            set
            {
                var old = _selectedMappedDevice;
                if (SetProperty(ref _selectedMappedDevice, value))
                {
                    if (old != null) old.PropertyChanged -= OnSelectedDevicePropertyChanged;
                    if (value != null) value.PropertyChanged += OnSelectedDevicePropertyChanged;
                    OnPropertyChanged(nameof(HasSelectedDevice));
                    OnPropertyChanged(nameof(SelectedDeviceHasSpeaker));
                    OnPropertyChanged(nameof(SelectedDeviceHasNoSpeaker));
                    OnPropertyChanged(nameof(SelectedDeviceHasHapticTones));
                    OnPropertyChanged(nameof(SelectedDeviceHasTouchpadPulse));
                    // Pointer-tab tunables (IrSensorBarPos etc.) are per
                    // (device, slot) on PadSetting; the device-switch reload
                    // repopulates them through the same PadSetting load path
                    // as the gyro tunables.
                    // The mirror source is per device; re-point the combo at
                    // the newly selected device's value.
                    if (SelectedConfigTab == AudioTabIndex) RefreshMirrorSources();
                    else OnPropertyChanged(nameof(SelectedMirrorSourceId));
                    // Swap the Lighting tab's bound config to the new
                    // device's per-device entry. UI bindings that resolve
                    // DeviceConfig.* re-evaluate against the new
                    // reference.
                    BindDeviceConfigForDevice(value?.InstanceGuid ?? Guid.Empty);
                    // The engage picker projection reads the NEW device's
                    // config, so re-resolve after the config swap (#185).
                    OnPropertyChanged(nameof(MirrorEngageSelectedInput));
                    SelectedDeviceChanged?.Invoke(this, value);
                }
            }
        }

        /// <summary>Whether a device is selected for configuration.</summary>
        public bool HasSelectedDevice => _selectedMappedDevice != null;

        private void OnSelectedDevicePropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MappedDeviceInfo.IsOnline))
                _mapAllCommand?.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// Raised when the user selects a different device within this slot.
        /// InputService should reload the PadSetting for the newly selected device.
        /// </summary>
        public event EventHandler<MappedDeviceInfo> SelectedDeviceChanged;

        /// <summary>
        /// Forces the same notification chain the setter would fire when
        /// <see cref="SelectedMappedDevice"/>'s underlying device changes
        /// without the object reference itself changing. InputService's
        /// SyncMappedDevices mutates existing MappedDeviceInfo entries in
        /// place to minimize ObservableCollection churn, so unassigning a
        /// device that sat above the previously-selected one (e.g. removing
        /// "All Keyboards (Merged)" from a slot that also has a DualSense)
        /// rewrites the selected entry's Name + InstanceGuid + IsOnline
        /// while keeping the same object — without this nudge, listeners
        /// that gate on PropertyChanged (PadPage.SyncTabVisibility, the
        /// FFB / Lighting / Adaptive Triggers tabs, the HM dispatcher
        /// re-attachment) miss the device identity swap and stay pinned
        /// to the prior device's capabilities.
        /// </summary>
        public void NotifySelectedMappedDeviceIdentityChanged()
        {
            BindDeviceConfigForDevice(_selectedMappedDevice?.InstanceGuid ?? Guid.Empty);
            OnPropertyChanged(nameof(HasSelectedDevice));
            OnPropertyChanged(nameof(SelectedMappedDevice));
            SelectedDeviceChanged?.Invoke(this, _selectedMappedDevice);
        }

        private string _mappedDeviceName = Strings.Instance.Mapping_NoDeviceMapped;

        public string MappedDeviceName
        {
            get => _mappedDeviceName;
            set => SetProperty(ref _mappedDeviceName, value);
        }

        private Guid _mappedDeviceGuid;

        public Guid MappedDeviceGuid
        {
            get => _mappedDeviceGuid;
            set => SetProperty(ref _mappedDeviceGuid, value);
        }

        private bool _isDeviceOnline;

        public bool IsDeviceOnline
        {
            get => _isDeviceOnline;
            set => SetProperty(ref _isDeviceOnline, value);
        }

        // ═══════════════════════════════════════════════
        //  XInput output state (for visualizer) — unchanged
        // ═══════════════════════════════════════════════

        private bool _buttonA;
        public bool ButtonA { get => _buttonA; set => SetProperty(ref _buttonA, value); }

        private bool _buttonB;
        public bool ButtonB { get => _buttonB; set => SetProperty(ref _buttonB, value); }

        private bool _buttonX;
        public bool ButtonX { get => _buttonX; set => SetProperty(ref _buttonX, value); }

        private bool _buttonY;
        public bool ButtonY { get => _buttonY; set => SetProperty(ref _buttonY, value); }

        private bool _leftShoulder;
        public bool LeftShoulder { get => _leftShoulder; set => SetProperty(ref _leftShoulder, value); }

        private bool _rightShoulder;
        public bool RightShoulder { get => _rightShoulder; set => SetProperty(ref _rightShoulder, value); }

        private bool _buttonBack;
        public bool ButtonBack { get => _buttonBack; set => SetProperty(ref _buttonBack, value); }

        private bool _buttonStart;
        public bool ButtonStart { get => _buttonStart; set => SetProperty(ref _buttonStart, value); }

        private bool _leftThumbButton;
        public bool LeftThumbButton { get => _leftThumbButton; set => SetProperty(ref _leftThumbButton, value); }

        private bool _rightThumbButton;
        public bool RightThumbButton { get => _rightThumbButton; set => SetProperty(ref _rightThumbButton, value); }

        private bool _buttonGuide;
        public bool ButtonGuide { get => _buttonGuide; set => SetProperty(ref _buttonGuide, value); }

        private bool _buttonShare;
        /// <summary>Xbox Series Share button live state. Mirrored from
        /// <see cref="Gamepad.Share"/> in <c>UpdateFromGamepad</c>; drives
        /// 2D overlay + 3D mesh accent on press.</summary>
        public bool ButtonShare { get => _buttonShare; set => SetProperty(ref _buttonShare, value); }

        private bool _dpadUp;
        public bool DPadUp { get => _dpadUp; set => SetProperty(ref _dpadUp, value); }

        private bool _dpadDown;
        public bool DPadDown { get => _dpadDown; set => SetProperty(ref _dpadDown, value); }

        private bool _dpadLeft;
        public bool DPadLeft { get => _dpadLeft; set => SetProperty(ref _dpadLeft, value); }

        private bool _dpadRight;
        public bool DPadRight { get => _dpadRight; set => SetProperty(ref _dpadRight, value); }

        private double _leftTrigger;
        public double LeftTrigger { get => _leftTrigger; set => SetProperty(ref _leftTrigger, value); }

        private double _rightTrigger;
        public double RightTrigger { get => _rightTrigger; set => SetProperty(ref _rightTrigger, value); }

        private double _thumbLX = 0.5;
        public double ThumbLX { get => _thumbLX; set => SetProperty(ref _thumbLX, value); }

        private double _thumbLY = 0.5;
        public double ThumbLY { get => _thumbLY; set => SetProperty(ref _thumbLY, value); }

        private double _thumbRX = 0.5;
        public double ThumbRX { get => _thumbRX; set => SetProperty(ref _thumbRX, value); }

        private double _thumbRY = 0.5;
        public double ThumbRY { get => _thumbRY; set => SetProperty(ref _thumbRY, value); }

        private short _rawThumbLX;
        public short RawThumbLX { get => _rawThumbLX; set => SetProperty(ref _rawThumbLX, value); }

        private short _rawThumbLY;
        public short RawThumbLY { get => _rawThumbLY; set => SetProperty(ref _rawThumbLY, value); }

        private short _rawThumbRX;
        public short RawThumbRX { get => _rawThumbRX; set => SetProperty(ref _rawThumbRX, value); }

        private short _rawThumbRY;
        public short RawThumbRY { get => _rawThumbRY; set => SetProperty(ref _rawThumbRY, value); }

        private ushort _rawLeftTrigger;
        public ushort RawLeftTrigger { get => _rawLeftTrigger; set => SetProperty(ref _rawLeftTrigger, value); }

        private ushort _rawRightTrigger;
        public ushort RawRightTrigger { get => _rawRightTrigger; set => SetProperty(ref _rawRightTrigger, value); }

        // ── Touchpad live state (PlayStation slot only — surfaced for the
        //    2D / 3D / web preview to render finger dots + click highlight) ──
        private float _touchpadFinger0X, _touchpadFinger0Y;
        private bool _touchpadFinger0Down;
        private float _touchpadFinger1X, _touchpadFinger1Y;
        private bool _touchpadFinger1Down;
        private bool _touchpadClickPressed;

        /// <summary>Finger 0 X (0..1, normalized to touchpad width).</summary>
        public float TouchpadFinger0X { get => _touchpadFinger0X; set => SetProperty(ref _touchpadFinger0X, value); }

        /// <summary>Finger 0 Y (0..1, normalized to touchpad height).</summary>
        public float TouchpadFinger0Y { get => _touchpadFinger0Y; set => SetProperty(ref _touchpadFinger0Y, value); }

        /// <summary>Finger 0 contact state.</summary>
        public bool TouchpadFinger0Down { get => _touchpadFinger0Down; set => SetProperty(ref _touchpadFinger0Down, value); }

        public float TouchpadFinger1X { get => _touchpadFinger1X; set => SetProperty(ref _touchpadFinger1X, value); }
        public float TouchpadFinger1Y { get => _touchpadFinger1Y; set => SetProperty(ref _touchpadFinger1Y, value); }
        public bool TouchpadFinger1Down { get => _touchpadFinger1Down; set => SetProperty(ref _touchpadFinger1Down, value); }

        /// <summary>True while the touchpad-click button is held (full-surface
        /// blue highlight in the previews).</summary>
        public bool TouchpadClickPressed { get => _touchpadClickPressed; set => SetProperty(ref _touchpadClickPressed, value); }

        // ── Per-device values for stick/trigger tab previews ──
        // These show the selected device only, not the combined slot.

        private double _deviceThumbLX = 0.5;
        public double DeviceThumbLX { get => _deviceThumbLX; set => SetProperty(ref _deviceThumbLX, value); }

        private double _deviceThumbLY = 0.5;
        public double DeviceThumbLY { get => _deviceThumbLY; set => SetProperty(ref _deviceThumbLY, value); }

        private double _deviceThumbRX = 0.5;
        public double DeviceThumbRX { get => _deviceThumbRX; set => SetProperty(ref _deviceThumbRX, value); }

        private double _deviceThumbRY = 0.5;
        public double DeviceThumbRY { get => _deviceThumbRY; set => SetProperty(ref _deviceThumbRY, value); }

        private short _deviceRawThumbLX;
        public short DeviceRawThumbLX { get => _deviceRawThumbLX; set => SetProperty(ref _deviceRawThumbLX, value); }

        private short _deviceRawThumbLY;
        public short DeviceRawThumbLY { get => _deviceRawThumbLY; set => SetProperty(ref _deviceRawThumbLY, value); }

        private short _deviceRawThumbRX;
        public short DeviceRawThumbRX { get => _deviceRawThumbRX; set => SetProperty(ref _deviceRawThumbRX, value); }

        private short _deviceRawThumbRY;
        public short DeviceRawThumbRY { get => _deviceRawThumbRY; set => SetProperty(ref _deviceRawThumbRY, value); }

        private double _deviceLeftTrigger;
        public double DeviceLeftTrigger { get => _deviceLeftTrigger; set => SetProperty(ref _deviceLeftTrigger, value); }

        private double _deviceRightTrigger;
        public double DeviceRightTrigger { get => _deviceRightTrigger; set => SetProperty(ref _deviceRightTrigger, value); }

        private ushort _deviceRawLeftTrigger;
        public ushort DeviceRawLeftTrigger { get => _deviceRawLeftTrigger; set => SetProperty(ref _deviceRawLeftTrigger, value); }

        private ushort _deviceRawRightTrigger;
        public ushort DeviceRawRightTrigger { get => _deviceRawRightTrigger; set => SetProperty(ref _deviceRawRightTrigger, value); }

        // ═══════════════════════════════════════════════
        //  Mapping rows — unchanged
        // ═══════════════════════════════════════════════

        public ObservableCollection<MappingItem> Mappings { get; } =
            new ObservableCollection<MappingItem>();

        /// <summary>Count of mapping rows currently eligible for the
        /// compact trivial rendering (#175 telemetry board). Drives the
        /// mono "{n} DIRECT" readout above the Mappings grid so the row
        /// compression is legible. Zero hides the readout.</summary>
        private int _directMappingCount;
        public int DirectMappingCount
        {
            get => _directMappingCount;
            private set => SetProperty(ref _directMappingCount, value);
        }

        // Rows currently subscribed for IsTrivialDirect changes. Tracked
        // explicitly because ObservableCollection.Clear() raises Reset
        // with no OldItems, which would otherwise leak the handlers.
        private readonly System.Collections.Generic.List<MappingItem> _directCountHooked
            = new System.Collections.Generic.List<MappingItem>();

        private void OnMappingsChangedForDirectCount(object sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                foreach (var m in _directCountHooked)
                    m.PropertyChanged -= OnMappingItemTrivialChanged;
                _directCountHooked.Clear();
                foreach (var m in Mappings)
                {
                    if (m == null) continue;
                    m.PropertyChanged += OnMappingItemTrivialChanged;
                    _directCountHooked.Add(m);
                }
            }
            else
            {
                if (e.OldItems != null)
                {
                    foreach (var o in e.OldItems)
                    {
                        if (o is MappingItem m)
                        {
                            m.PropertyChanged -= OnMappingItemTrivialChanged;
                            _directCountHooked.Remove(m);
                        }
                    }
                }
                if (e.NewItems != null)
                {
                    foreach (var n in e.NewItems)
                    {
                        if (n is MappingItem m)
                        {
                            m.PropertyChanged += OnMappingItemTrivialChanged;
                            _directCountHooked.Add(m);
                        }
                    }
                }
            }
            RefreshDirectMappingCount();
            // Row set changed, so the pipeline chips' cached owning rows
            // are stale. Flag the config recompute. The 1 s throttle in
            // RefreshPipelineChips covers in-place row edits.
            _pipelineChipsConfigDirty = true;
        }

        private void OnMappingItemTrivialChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MappingItem.IsTrivialDirect))
                RefreshDirectMappingCount();
        }

        private void RefreshDirectMappingCount()
        {
            int n = 0;
            foreach (var m in Mappings)
                if (m != null && m.IsTrivialDirect) n++;
            DirectMappingCount = n;
        }

        // ─────────────────────────────────────────────
        //  Pipeline heat chips (#175 item 10)
        //
        //  Five fixed chips above the Mappings grid, one per transform
        //  pipeline kind (CURVE / GYRO / SHIFT / INV / DZ). Active =
        //  the pipeline carries configuration; Live = an owning row's
        //  live value is past its gate this frame (rowfire flags set by
        //  InputService.UpdateMappingLiveValues, which also calls
        //  RefreshPipelineChips on the same 30 Hz Pad-page lane, so no
        //  extra timer). All UI-only session state, never persisted.
        // ─────────────────────────────────────────────

        private bool _pipelineCurveActive;
        public bool PipelineCurveActive
        {
            get => _pipelineCurveActive;
            set { if (SetProperty(ref _pipelineCurveActive, value)) OnPropertyChanged(nameof(HasAnyPipelineChips)); }
        }
        private bool _pipelineCurveLive;
        public bool PipelineCurveLive { get => _pipelineCurveLive; set => SetProperty(ref _pipelineCurveLive, value); }
        private System.Collections.Generic.IReadOnlyList<PipelineTipRow> _pipelineCurveTipRows = System.Array.Empty<PipelineTipRow>();
        public System.Collections.Generic.IReadOnlyList<PipelineTipRow> PipelineCurveTipRows { get => _pipelineCurveTipRows; private set => SetProperty(ref _pipelineCurveTipRows, value); }

        private bool _pipelineGyroActive;
        public bool PipelineGyroActive
        {
            get => _pipelineGyroActive;
            set { if (SetProperty(ref _pipelineGyroActive, value)) OnPropertyChanged(nameof(HasAnyPipelineChips)); }
        }
        private bool _pipelineGyroLive;
        public bool PipelineGyroLive { get => _pipelineGyroLive; set => SetProperty(ref _pipelineGyroLive, value); }
        private System.Collections.Generic.IReadOnlyList<PipelineTipRow> _pipelineGyroTipRows = System.Array.Empty<PipelineTipRow>();
        public System.Collections.Generic.IReadOnlyList<PipelineTipRow> PipelineGyroTipRows { get => _pipelineGyroTipRows; private set => SetProperty(ref _pipelineGyroTipRows, value); }

        private bool _pipelineShiftActive;
        public bool PipelineShiftActive
        {
            get => _pipelineShiftActive;
            set { if (SetProperty(ref _pipelineShiftActive, value)) OnPropertyChanged(nameof(HasAnyPipelineChips)); }
        }
        private bool _pipelineShiftLive;
        public bool PipelineShiftLive { get => _pipelineShiftLive; set => SetProperty(ref _pipelineShiftLive, value); }
        private string _pipelineShiftSummary = "";
        public string PipelineShiftSummary { get => _pipelineShiftSummary; set => SetProperty(ref _pipelineShiftSummary, value ?? ""); }

        private bool _pipelineInvertActive;
        public bool PipelineInvertActive
        {
            get => _pipelineInvertActive;
            set { if (SetProperty(ref _pipelineInvertActive, value)) OnPropertyChanged(nameof(HasAnyPipelineChips)); }
        }
        private bool _pipelineInvertLive;
        public bool PipelineInvertLive { get => _pipelineInvertLive; set => SetProperty(ref _pipelineInvertLive, value); }
        private System.Collections.Generic.IReadOnlyList<PipelineTipRow> _pipelineInvertTipRows = System.Array.Empty<PipelineTipRow>();
        public System.Collections.Generic.IReadOnlyList<PipelineTipRow> PipelineInvertTipRows { get => _pipelineInvertTipRows; private set => SetProperty(ref _pipelineInvertTipRows, value); }

        private bool _pipelineDeadZoneActive;
        public bool PipelineDeadZoneActive
        {
            get => _pipelineDeadZoneActive;
            set { if (SetProperty(ref _pipelineDeadZoneActive, value)) OnPropertyChanged(nameof(HasAnyPipelineChips)); }
        }
        private bool _pipelineDeadZoneLive;
        public bool PipelineDeadZoneLive { get => _pipelineDeadZoneLive; set => SetProperty(ref _pipelineDeadZoneLive, value); }
        private System.Collections.Generic.IReadOnlyList<PipelineTipRow> _pipelineDeadZoneTipRows = System.Array.Empty<PipelineTipRow>();
        public System.Collections.Generic.IReadOnlyList<PipelineTipRow> PipelineDeadZoneTipRows { get => _pipelineDeadZoneTipRows; private set => SetProperty(ref _pipelineDeadZoneTipRows, value); }

        /// <summary>Chip-row visibility gate: collapse the whole strip
        /// when no pipeline carries configuration.</summary>
        public bool HasAnyPipelineChips =>
            _pipelineCurveActive || _pipelineGyroActive || _pipelineShiftActive
            || _pipelineInvertActive || _pipelineDeadZoneActive;

        /// <summary>Row owns the INV pipeline: a mapped primary with the
        /// invert transform on. Extra-source Invert is excluded on
        /// purpose. On a bipolar pair it encodes the negative direction,
        /// not an inversion transform.</summary>
        public static bool IsInvertPipelineRow(MappingItem m) =>
            m != null && m.IsMapped && m.IsInverted;

        /// <summary>Row owns the DZ pipeline: per-mapping deadzone moved
        /// off its 50% default (MappingItem initializer +
        /// ResetDeadZoneCommand) on a row where it applies.</summary>
        public static bool IsDeadZonePipelineRow(MappingItem m) =>
            m != null && m.IsDeadZoneApplicable && m.MappingDeadZone != 50;

        /// <summary>Row owns the GYRO pipeline: primary or any extra
        /// source reads a "Gyro *" axis. Motion-passthrough rows use
        /// "Motion *" descriptors, so the auto-created Sony passthrough
        /// never lights this chip.</summary>
        public static bool IsGyroPipelineRow(MappingItem m)
        {
            if (m == null) return false;
            if (m.IsGyroSource) return true;
            foreach (var s in m.ExtraSources)
            {
                if (s != null && !string.IsNullOrEmpty(s.Descriptor)
                    && s.Descriptor.StartsWith("Gyro ", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>Row owns the CURVE pipeline: its target is one of the
        /// six curve-bearing outputs and that output's sensitivity curve
        /// is non-linear (defaults "0" / "0,0;1,1" both parse linear via
        /// CurveLut.IsLinear).</summary>
        public bool IsCurvePipelineRow(MappingItem m)
        {
            if (m == null) return false;
            string curve = CurveStringForTarget(m.TargetSettingName);
            return curve != null && !Common.CurveLut.IsLinear(curve);
        }

        private string CurveStringForTarget(string target) => target switch
        {
            "LeftThumbAxisX" => LeftSensitivityCurveX,
            "LeftThumbAxisY" => LeftSensitivityCurveY,
            "RightThumbAxisX" => RightSensitivityCurveX,
            "RightThumbAxisY" => RightSensitivityCurveY,
            "LeftTrigger" => LeftTriggerSensitivityCurve,
            "RightTrigger" => RightTriggerSensitivityCurve,
            _ => null,
        };

        // Config-derived chip state (Active flags, tooltip listings,
        // owning rows, MatchPreset lookups) only moves on user edits, so it
        // recomputes on a dirty flag (row add/remove) or at most once
        // per second, the same 1 s cadence the dashboard's stage ledger
        // rides. The 30 Hz tick path below only reads rowfire flags.
        private bool _pipelineChipsConfigDirty = true;
        private long _pipelineChipsConfigTick;
        private readonly System.Collections.Generic.List<MappingItem> _pipelineCurveRows = new();
        private readonly System.Collections.Generic.List<MappingItem> _pipelineGyroRows = new();
        private readonly System.Collections.Generic.List<MappingItem> _pipelineInvertRows = new();
        private readonly System.Collections.Generic.List<MappingItem> _pipelineDeadZoneRows = new();
        private string _pipelineShiftIdleSummary = "";

        // MatchPreset memo. Each miss costs seven parse/serialize
        // passes (Normalize per preset), so the config recompute reads
        // the cached name for a seen curve string. Preset names are
        // localized, so OnCultureChanged clears the cache. UI thread only.
        private static readonly System.Collections.Generic.Dictionary<string, string> _matchPresetCache = new();
        private static string MatchPresetCached(string curve)
        {
            curve ??= string.Empty;
            if (!_matchPresetCache.TryGetValue(curve, out var name))
            {
                name = Common.CurveLut.MatchPreset(curve);
                _matchPresetCache[curve] = name;
            }
            return name;
        }

        /// <summary>Refreshes chip liveness from the current Mappings
        /// rows. Called by InputService.UpdateMappingLiveValues right
        /// after the rowfire pass so IsInputActive reflects this tick.
        /// The engaged layer mask comes from the same engine poll the
        /// shift flyout uses (InputManager.GetEngagedLayerMask).
        /// Presence + tooltip listings ride the throttled config
        /// recompute, not this per-tick path.</summary>
        public void RefreshPipelineChips(string engagedLayerMask)
        {
            long tick = Environment.TickCount64;
            if (_pipelineChipsConfigDirty || tick - _pipelineChipsConfigTick >= 1000)
            {
                _pipelineChipsConfigDirty = false;
                _pipelineChipsConfigTick = tick;
                RecomputePipelineChipConfig();
            }

            // Per-tick lane: liveness booleans only, OR-ed over the
            // cached owning rows' rowfire flags.
            bool curveLive = false, gyroLive = false, invertLive = false, dzLive = false;
            foreach (var m in _pipelineCurveRows) curveLive |= m.IsInputActive;
            foreach (var m in _pipelineGyroRows) gyroLive |= m.IsInputActive;
            foreach (var m in _pipelineInvertRows) invertLive |= m.IsInputActive;
            foreach (var m in _pipelineDeadZoneRows) dzLive |= m.IsInputActive;
            PipelineCurveLive = PipelineCurveActive && curveLive;
            PipelineGyroLive = PipelineGyroActive && gyroLive;
            PipelineInvertLive = PipelineInvertActive && invertLive;
            PipelineDeadZoneLive = PipelineDeadZoneActive && dzLive;

            // SHIFT flames while the engine holds a non-Base layer. The
            // summary swaps between the engaged layer's name and the
            // cached idle layer list.
            bool shiftEngaged = PipelineShiftActive
                && !string.IsNullOrEmpty(engagedLayerMask)
                && !string.Equals(engagedLayerMask, "Base", StringComparison.Ordinal);
            PipelineShiftLive = shiftEngaged;
            if (shiftEngaged)
            {
                string engagedName = engagedLayerMask;
                foreach (var t in LayerTabs)
                {
                    if (t != null && string.Equals(t.LayerMask, engagedLayerMask, StringComparison.Ordinal))
                    { engagedName = t.LayerName; break; }
                }
                PipelineShiftSummary = engagedName;
            }
            else
            {
                PipelineShiftSummary = _pipelineShiftIdleSummary;
            }
        }

        /// <summary>Recomputes the config-derived chip state: which
        /// rows own each pipeline, the Active flags, and the tooltip
        /// row listings (including the MatchPreset names). Runs on the
        /// dirty flag / 1 s throttle inside RefreshPipelineChips, never
        /// per tick.</summary>
        private void RecomputePipelineChipConfig()
        {
            _pipelineCurveRows.Clear();
            _pipelineGyroRows.Clear();
            _pipelineInvertRows.Clear();
            _pipelineDeadZoneRows.Clear();

            foreach (var m in Mappings)
            {
                if (m == null) continue;
                if (IsCurvePipelineRow(m)) _pipelineCurveRows.Add(m);
                if (IsGyroPipelineRow(m)) _pipelineGyroRows.Add(m);
                if (IsInvertPipelineRow(m)) _pipelineInvertRows.Add(m);
                if (IsDeadZonePipelineRow(m)) _pipelineDeadZoneRows.Add(m);
            }

            PipelineCurveActive = _pipelineCurveRows.Count > 0;
            PipelineGyroActive = _pipelineGyroRows.Count > 0;
            PipelineInvertActive = _pipelineInvertRows.Count > 0;
            PipelineDeadZoneActive = _pipelineDeadZoneRows.Count > 0;

            // Structured tooltip listings (user report 2026-07-05: the
            // old single-string summary ran the whole controller's rows
            // on one long line, with no device attribution). Swap only
            // on content change: this recompute runs every second, and
            // replacing an equal list would re-render an open tooltip.
            var curveTip = BuildPipelineTipRows(_pipelineCurveRows,
                m => MatchPresetCached(CurveStringForTarget(m.TargetSettingName)));
            if (!TipRowsEqual(_pipelineCurveTipRows, curveTip)) PipelineCurveTipRows = curveTip;
            var gyroTip = BuildPipelineTipRows(_pipelineGyroRows, null);
            if (!TipRowsEqual(_pipelineGyroTipRows, gyroTip)) PipelineGyroTipRows = gyroTip;
            var invertTip = BuildPipelineTipRows(_pipelineInvertRows, null);
            if (!TipRowsEqual(_pipelineInvertTipRows, invertTip)) PipelineInvertTipRows = invertTip;
            var dzTip = BuildPipelineTipRows(_pipelineDeadZoneRows, m => m.MappingDeadZone + "%");
            if (!TipRowsEqual(_pipelineDeadZoneTipRows, dzTip)) PipelineDeadZoneTipRows = dzTip;

            // SHIFT: present when the slot has any layer beyond Base.
            bool shiftActive = LayerTabs.Count > 1;
            PipelineShiftActive = shiftActive;
            if (shiftActive)
            {
                var names = new System.Collections.Generic.List<string>();
                foreach (var t in LayerTabs)
                    if (t != null && !t.IsBase) names.Add(t.LayerName);
                _pipelineShiftIdleSummary = string.Join(" · ", names);
            }
            else
            {
                _pipelineShiftIdleSummary = "";
            }
        }

        /// <summary>One line of a pipeline chip tooltip: a device
        /// header (present only when several controllers feed the
        /// chip's row set), an "OUTPUT ← source" mapping line, or the
        /// "+N" overflow tail. Built on the 1 s config lane.</summary>
        public sealed class PipelineTipRow
        {
            public bool IsHeader { get; init; }
            public bool IsTail { get; init; }
            /// <summary>Header / tail lane text: device label or "+N".</summary>
            public string Text { get; init; } = "";
            /// <summary>Mapping lane: the VC output label.</summary>
            public string Output { get; init; } = "";
            /// <summary>Mapping lane: feeding source(s), plus the
            /// chip-specific note (curve preset name, DZ percent).</summary>
            public string Detail { get; init; } = "";
        }

        // Tooltips cannot scroll, so the listing bounds its own height:
        // past this many mapping lines the list ends in a "+N" tail.
        private const int PipelineTipRowCap = 20;

        /// <summary>Builds one chip's tooltip listing: one line per
        /// owning row, bucketed under the primary feeding device's
        /// label when more than one device feeds the set. The mapping
        /// model carries exactly one primary device per row; extra
        /// sources hold their own labels and ride the row's source
        /// cell inline (TipSourceText), so the primary is the honest
        /// grouping key.</summary>
        private static System.Collections.Generic.IReadOnlyList<PipelineTipRow> BuildPipelineTipRows(
            System.Collections.Generic.List<MappingItem> rows,
            System.Func<MappingItem, string> note)
        {
            // Bucket by primary device label, first-seen order. Rows
            // with no primary device (config carried by an unmapped
            // target) lead the list without a header.
            var bucketDevs = new System.Collections.Generic.List<string>();
            var bucketRows = new System.Collections.Generic.List<System.Collections.Generic.List<MappingItem>>();
            foreach (var m in rows)
            {
                string dev = (m.PrimarySourceDeviceLabel ?? "").Trim();
                int idx = bucketDevs.IndexOf(dev);
                if (idx < 0)
                {
                    idx = dev.Length == 0 ? 0 : bucketDevs.Count;
                    bucketDevs.Insert(idx, dev);
                    bucketRows.Insert(idx, new System.Collections.Generic.List<MappingItem>());
                }
                bucketRows[idx].Add(m);
            }

            int named = 0;
            foreach (var d in bucketDevs) if (d.Length > 0) named++;
            bool grouped = bucketDevs.Count > 1 && named > 0;

            var tip = new System.Collections.Generic.List<PipelineTipRow>();
            int emitted = 0, overflow = 0;
            for (int b = 0; b < bucketDevs.Count; b++)
            {
                // Header only when at least one of the bucket's rows
                // will actually render below it.
                if (grouped && bucketDevs[b].Length > 0 && emitted < PipelineTipRowCap)
                    tip.Add(new PipelineTipRow { IsHeader = true, Text = bucketDevs[b] });
                foreach (var m in bucketRows[b])
                {
                    if (emitted >= PipelineTipRowCap) { overflow++; continue; }
                    string src = TipSourceText(m, bucketDevs[b]);
                    string extra = note?.Invoke(m) ?? "";
                    tip.Add(new PipelineTipRow
                    {
                        Output = m.TargetLabel ?? "",
                        Detail = extra.Length == 0 ? src
                               : src.Length == 0 ? extra
                               : src + " · " + extra,
                    });
                    emitted++;
                }
            }
            if (overflow > 0)
                tip.Add(new PipelineTipRow { IsTail = true, Text = "+" + overflow });
            return tip;
        }

        /// <summary>Source cell for one tooltip line: the primary
        /// source text plus every named extra source. An extra from a
        /// different device carries its "device: control" prefix
        /// inline, the same grammar as the preview annotation's
        /// BuildAnnotationWireRows.</summary>
        private static string TipSourceText(MappingItem m, string primaryDevice)
        {
            var parts = new System.Collections.Generic.List<string>();
            string primary = (m.SourceDisplayText ?? "").Trim();
            if (primary.Length > 0) parts.Add(primary);
            foreach (var s in m.ExtraSources)
            {
                if (s == null) continue;
                string name = (s.SelectedInput?.DisplayName ?? s.Descriptor ?? "").Trim();
                if (name.Length == 0) continue;
                string dev = (s.DisplayDeviceLabel ?? "").Trim();
                parts.Add(dev.Length > 0 && !string.Equals(dev, primaryDevice, StringComparison.Ordinal)
                    ? dev + ": " + name : name);
            }
            return string.Join(" + ", parts);
        }

        /// <summary>Content equality for tooltip listings, so the 1 s
        /// recompute only publishes a genuinely changed list.</summary>
        private static bool TipRowsEqual(
            System.Collections.Generic.IReadOnlyList<PipelineTipRow> a,
            System.Collections.Generic.IReadOnlyList<PipelineTipRow> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                var x = a[i];
                var y = b[i];
                if (x.IsHeader != y.IsHeader || x.IsTail != y.IsTail
                    || !string.Equals(x.Text, y.Text, StringComparison.Ordinal)
                    || !string.Equals(x.Output, y.Output, StringComparison.Ordinal)
                    || !string.Equals(x.Detail, y.Detail, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        /// <summary>Drops every flame (Live flags) without touching the
        /// config-derived Active / Summary state. InputService.Stop
        /// calls this after tearing down the UI timer so no chip stays
        /// lit on a dead engine.</summary>
        public void ClearPipelineLiveness()
        {
            PipelineCurveLive = false;
            PipelineGyroLive = false;
            PipelineInvertLive = false;
            PipelineDeadZoneLive = false;
            PipelineShiftLive = false;
            PipelineShiftSummary = _pipelineShiftIdleSummary;
        }

        /// <summary>
        /// True when <see cref="Mappings"/> currently reflects this slot's
        /// authoritative MappingSet — i.e. <c>RefreshMappingsCore</c> has run
        /// since the MappingSet was last rebuilt. False during the window
        /// between a MappingSet rebuild (e.g. a device assignment auto-mapping
        /// a new pad) and the ViewModel reload that follows it.
        ///
        /// <para><b>Why this exists.</b> <c>SaveViewModelToPadSetting</c>
        /// (syncMappings) persists the per-VC mappings by CLEARING every slot
        /// device's PadSetting descriptors and rewriting them from
        /// <see cref="Mappings"/>. That is only lossless when <see cref="Mappings"/>
        /// is current. During an assignment the dropdown auto-selects the
        /// just-assigned device, which fires <c>OnSelectedDeviceChanged</c> →
        /// <c>SaveViewModelToPadSetting</c> a few milliseconds BEFORE
        /// <c>RefreshMappingsToViewModel</c> reloads <see cref="Mappings"/> from
        /// the freshly auto-mapped MappingSet. With a stale (empty)
        /// <see cref="Mappings"/>, the clear+rewrite wiped the new device's
        /// entire auto-map — the trace showed a DualSense going from 21 mapped
        /// descriptors to 0 in 2 ms ("assigning my DualSense to the wheel's slot
        /// only maps the Share button"). When this flag is false, the save skips
        /// the mapping clobber (the MappingSet is authoritative and already
        /// current); per-device tuning still saves.</para>
        /// </summary>
        public bool MappingsViewLoaded { get; set; }

        /// <summary>
        /// Raised after RebuildMappings completes so listeners (e.g. InputService) can
        /// reload mapping descriptors from the active PadSetting into the new MappingItems.
        /// </summary>
        public event EventHandler MappingsRebuilt;

        // ── Shift mode (Issue #61 Phase 6) ──

        /// <summary>Layer mask currently being authored on the Mappings tab.
        /// Defaults to <c>"Base"</c> (no shift layer selected). Setting this
        /// fires <see cref="LayerActivated"/> so InputService reloads each
        /// MappingItem from the matching layer's MappingRows.</summary>
        private string _activeLayerMask = "Base";
        public string ActiveLayerMask
        {
            get => _activeLayerMask;
            set
            {
                var v = value ?? "Base";
                if (SetProperty(ref _activeLayerMask, v))
                {
                    foreach (var t in LayerTabs)
                        t.IsActive = string.Equals(t.LayerMask, v, StringComparison.Ordinal);
                    OnPropertyChanged(nameof(IsActiveLayerInheriting));
                    OnPropertyChanged(nameof(ActiveLayerColor));
                    LayerActivated?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>Raised when <see cref="ActiveLayerMask"/> changes. Subscribers
        /// reload per-row source data so the DataGrid reflects the picked
        /// layer's rows instead of the previous layer's.</summary>
        public event EventHandler LayerActivated;

        /// <summary>Authored color hint of the layer currently being
        /// authored ("#AARRGGBB" or empty when unset). The Mappings
        /// subpanel rail tints to it so expanded details read as belonging
        /// to the picked layer (#175). Recomputed on
        /// <see cref="ActiveLayerMask"/> change and
        /// <see cref="RebuildLayerTabs"/>.</summary>
        public string ActiveLayerColor
        {
            get
            {
                foreach (var t in LayerTabs)
                    if (t.IsActive) return t.Color ?? "";
                return "";
            }
        }

        /// <summary>Tab strip backing collection. Always starts with the Base
        /// tab; each shift layer authored on this slot adds an entry.
        /// Populated by <see cref="RebuildLayerTabs"/> from
        /// <c>SettingsManager.SlotMappingSets[PadIndex].ShiftActivators</c>.</summary>
        public ObservableCollection<ShiftLayerInfo> LayerTabs { get; } = new();

        /// <summary>True when the slot has at least one shift activator
        /// authored, i.e. at least one tab beyond Base. Drives the nested
        /// tab strip's visibility (basic users without any shift layers see
        /// the Mappings tab exactly as before).</summary>
        public bool HasShiftLayers => LayerTabs.Count > 1;

        /// <summary>True when the currently-active layer is a shift layer
        /// AND its activator has <c>InheritUnmapped=true</c>. Drives the
        /// per-row "Do not inherit" CheckBox visibility — that flag only
        /// has meaning on inheritance-enabled layers (in replace mode the
        /// Base row is already blocked wholesale). Recomputed by
        /// <see cref="RebuildLayerTabs"/> and on
        /// <see cref="ActiveLayerMask"/> change.</summary>
        public bool IsActiveLayerInheriting
        {
            get
            {
                if (string.Equals(_activeLayerMask, "Base", StringComparison.Ordinal))
                    return false;
                var sets = PadForge.Common.Input.SettingsManager.SlotMappingSets;
                if (PadIndex < 0 || PadIndex >= sets.Length) return false;
                var ms = sets[PadIndex];
                if (ms?.ShiftActivators == null) return false;
                foreach (var a in ms.ShiftActivators)
                {
                    if (a == null) continue;
                    if (!string.Equals(a.LayerMask, _activeLayerMask, StringComparison.Ordinal)) continue;
                    return a.InheritUnmapped;
                }
                return false;
            }
        }

        /// <summary>Rebuilds <see cref="LayerTabs"/> from the supplied
        /// activator list (which the caller pulls from
        /// <c>SettingsManager.SlotMappingSets[PadIndex].ShiftActivators</c>).
        /// Called whenever the slot's activator list changes — add, edit,
        /// delete, paste-layer. Preserves <see cref="ActiveLayerMask"/>
        /// when the active mask still exists in the new tab set, otherwise
        /// falls back to Base.</summary>
        public void RebuildLayerTabs(
            System.Collections.Generic.IReadOnlyList<PadForge.Engine.Data.ShiftActivator> activators)
        {
            LayerTabs.Clear();
            // Base flyout/tab appearance (#119) lives on the slot's MappingSet,
            // since Base has no activator. Empty name falls back to the label.
            string baseName = PadForge.Resources.Strings.Strings.Instance.Pad_Shift_BaseTabLabel;
            string baseColor = "";
            var slotSets = PadForge.Common.Input.SettingsManager.SlotMappingSets;
            if (slotSets != null && PadIndex >= 0 && PadIndex < slotSets.Length)
            {
                var slotMs = slotSets[PadIndex];
                if (slotMs != null)
                {
                    if (!string.IsNullOrEmpty(slotMs.BaseLayerName)) baseName = slotMs.BaseLayerName;
                    baseColor = slotMs.BaseColor ?? "";
                }
            }
            LayerTabs.Add(new ShiftLayerInfo
            {
                LayerMask = "Base",
                LayerName = baseName,
                Color = baseColor,
                IsActive = string.Equals(_activeLayerMask, "Base", StringComparison.Ordinal),
            });
            bool activeFound = LayerTabs[0].IsActive;
            if (activators != null)
            {
                foreach (var a in activators)
                {
                    if (a == null || string.IsNullOrEmpty(a.LayerMask)) continue;
                    var info = new ShiftLayerInfo
                    {
                        LayerMask = a.LayerMask,
                        LayerName = string.IsNullOrEmpty(a.LayerName) ? a.LayerMask : a.LayerName,
                        Color = a.Color ?? "",
                        IsActive = string.Equals(_activeLayerMask, a.LayerMask, StringComparison.Ordinal),
                    };
                    if (info.IsActive) activeFound = true;
                    LayerTabs.Add(info);
                }
            }
            if (!activeFound)
            {
                // Active layer no longer exists; snap back to Base.
                _activeLayerMask = "Base";
                LayerTabs[0].IsActive = true;
                OnPropertyChanged(nameof(ActiveLayerMask));
            }
            OnPropertyChanged(nameof(HasShiftLayers));
            OnPropertyChanged(nameof(IsActiveLayerInheriting));
            OnPropertyChanged(nameof(ActiveLayerColor));
        }

        /// <summary>
        /// Rebuilds the Mappings collection based on the current OutputType and Extended config.
        /// Labels follow the output type's convention (Xbox / PlayStation / Extended numbered).
        /// </summary>
        public void RebuildMappings()
        {
            Mappings.Clear();

            // Extended uses the dynamic Extended-style layout in v3: the
            // HIDMaestro profile defines axis/button counts and no two
            // profiles share the same layout. Xbox / PlayStation keep
            // fixed gamepad grids. KeyboardMouse and MIDI have their own
            // mapping shapes.
            if (OutputType == VirtualControllerType.KeyboardMouse)
                InitializeKeyboardMouseMappings();
            else if (OutputType == VirtualControllerType.Midi)
                InitializeMidiMappings();
            else if (OutputType == VirtualControllerType.Extended)
                InitializeExtendedCustomMappings();
            else
                InitializeGamepadMappings();

            // Menus (#9 B-17) live on the same slot MappingSet the rows
            // do, so every path that rebuilds the mapping view (profile
            // apply, Workshop import, output-type change, Reset to
            // Defaults) refreshes the Menus tab in the same breath.
            ReloadMenus();
            // The rumble-to-audio config (#236) shares that lifetime, so
            // the Bass Shakers tab re-seeds from the set on the same paths.
            ReloadRumbleAudio();
            // The SOCD card (#240) lives on the same slot MappingSet and
            // re-seeds on the same paths.
            ReloadSocd();

            MappingsRebuilt?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Standard gamepad mappings (21 items). Xbox → Xbox 360 labels,
        /// PlayStation → DualShock 4 labels.
        /// </summary>
        private void InitializeGamepadMappings()
        {
            bool isPlayStation = OutputType == VirtualControllerType.PlayStation;

            // Buttons
            if (isPlayStation)
            {
                Mappings.Add(new MappingItem("\u2715", "ButtonA", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("\u25CB", "ButtonB", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("\u25FB", "ButtonX", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("\u25B3", "ButtonY", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("L1", "LeftShoulder", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("R1", "RightShoulder", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("Share", "ButtonBack", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("Options", "ButtonStart", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("PS", "ButtonGuide", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("L3", "LeftThumbButton", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("R3", "RightThumbButton", MappingCategory.Buttons));
            }
            else
            {
                Mappings.Add(new MappingItem("A", "ButtonA", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("B", "ButtonB", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("X", "ButtonX", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("Y", "ButtonY", MappingCategory.Buttons));
                Mappings.Add(new MappingItem(Strings.Instance.Btn_LeftShoulder, "LeftShoulder", MappingCategory.Buttons));
                Mappings.Add(new MappingItem(Strings.Instance.Btn_RightShoulder, "RightShoulder", MappingCategory.Buttons));
                Mappings.Add(new MappingItem(Strings.Instance.Btn_Back, "ButtonBack", MappingCategory.Buttons));
                Mappings.Add(new MappingItem(Strings.Instance.Btn_Start, "ButtonStart", MappingCategory.Buttons));
                Mappings.Add(new MappingItem(Strings.Instance.Btn_Guide, "ButtonGuide", MappingCategory.Buttons));

                // Share — Xbox Series profiles only. Sits after Guide,
                // before the stick buttons. HM drops the bit on profiles
                // that don't declare button 13, so limiting the UI row to
                // Series-only avoids surfacing a non-functional slot on
                // Xbox 360 / Xbox One profiles.
                if (!string.IsNullOrEmpty(ProfileId) &&
                    ProfileId.StartsWith("xbox-series-", StringComparison.OrdinalIgnoreCase))
                {
                    Mappings.Add(new MappingItem("Share", "ButtonShare", MappingCategory.Buttons,
                        includeInMapAll: false));
                }

                Mappings.Add(new MappingItem(Strings.Instance.Btn_LeftStickButton, "LeftThumbButton", MappingCategory.Buttons));
                Mappings.Add(new MappingItem(Strings.Instance.Btn_RightStickButton, "RightThumbButton", MappingCategory.Buttons));
            }

            // D-Pad
            Mappings.Add(new MappingItem(Strings.Instance.Btn_DPadUp, "DPadUp", MappingCategory.DPad));
            Mappings.Add(new MappingItem(Strings.Instance.Btn_DPadDown, "DPadDown", MappingCategory.DPad));
            Mappings.Add(new MappingItem(Strings.Instance.Btn_DPadLeft, "DPadLeft", MappingCategory.DPad));
            Mappings.Add(new MappingItem(Strings.Instance.Btn_DPadRight, "DPadRight", MappingCategory.DPad));

            // Triggers
            Mappings.Add(new MappingItem(isPlayStation ? "L2" : Strings.Instance.Btn_LeftTrigger, "LeftTrigger", MappingCategory.Triggers));
            Mappings.Add(new MappingItem(isPlayStation ? "R2" : Strings.Instance.Btn_RightTrigger, "RightTrigger", MappingCategory.Triggers));

            // Stick axes
            Mappings.Add(new MappingItem(Strings.Instance.Btn_LeftStickX, "LeftThumbAxisX", MappingCategory.LeftStick, "LeftThumbAxisXNeg"));
            Mappings.Add(new MappingItem(Strings.Instance.Btn_LeftStickY, "LeftThumbAxisY", MappingCategory.LeftStick, "LeftThumbAxisYNeg"));
            Mappings.Add(new MappingItem(Strings.Instance.Btn_RightStickX, "RightThumbAxisX", MappingCategory.RightStick, "RightThumbAxisXNeg"));
            Mappings.Add(new MappingItem(Strings.Instance.Btn_RightStickY, "RightThumbAxisY", MappingCategory.RightStick, "RightThumbAxisYNeg"));

            // Touchpad (PlayStation only)
            if (isPlayStation)
            {
                // The virtual DualSense / DS4 exposes one touchpad with two
                // fingers. Labels use the explicit "Touchpad {pad} Finger
                // {finger}" format (pad 1, fingers 1-2) so the output targets
                // read the same way as the physical-device picker.
                Mappings.Add(new MappingItem(string.Format(Strings.Instance.Mapping_TouchpadFingerX_Format,     1, 1), "TouchpadX1", MappingCategory.Touchpad));
                Mappings.Add(new MappingItem(string.Format(Strings.Instance.Mapping_TouchpadFingerY_Format,     1, 1), "TouchpadY1", MappingCategory.Touchpad));
                Mappings.Add(new MappingItem(string.Format(Strings.Instance.Mapping_TouchpadFingerX_Format,     1, 2), "TouchpadX2", MappingCategory.Touchpad));
                Mappings.Add(new MappingItem(string.Format(Strings.Instance.Mapping_TouchpadFingerY_Format,     1, 2), "TouchpadY2", MappingCategory.Touchpad));
                Mappings.Add(new MappingItem(string.Format(Strings.Instance.Mapping_TouchpadFingerTouch_Format, 1, 1), "TouchpadContact1", MappingCategory.Touchpad));
                Mappings.Add(new MappingItem(string.Format(Strings.Instance.Mapping_TouchpadFingerTouch_Format, 1, 2), "TouchpadContact2", MappingCategory.Touchpad));
                Mappings.Add(new MappingItem(Strings.Instance.Mapping_TouchpadClick, "TouchpadClick", MappingCategory.Buttons));

                // Motion passthrough — the virtual DualSense / DS4
                // exposes a 3-axis gyro + 3-axis accel HID report and
                // a DSU broadcast. These two rows let the user pick
                // which assigned device contributes each sensor stream
                // (delete a row → no contribution from that device on
                // that sub-channel). Auto-created on assignment for
                // gyro / accel-capable devices via EnsureMotionRows.
                Mappings.Add(new MappingItem(Strings.Instance.Mapping_MotionGyro,  "MotionGyro",  MappingCategory.Motion));
                Mappings.Add(new MappingItem(Strings.Instance.Mapping_MotionAccel, "MotionAccel", MappingCategory.Motion));
            }
        }

        /// <summary>
        /// MIDI mappings — labels use CC numbers for axes and Note numbers for buttons.
        /// Uses the same PadSetting property keys as gamepad so the pipeline works unchanged.
        /// </summary>
        private void InitializeMidiMappings()
        {
            var mc = MidiConfig;
            var ccNumbers = mc.GetCcNumbers();
            var noteNumbers = mc.GetNoteNumbers();

            // CC outputs — each CC is a bipolar axis with positive and negative mapping keys.
            for (int i = 0; i < mc.CcCount; i++)
                Mappings.Add(new MappingItem($"CC {ccNumbers[i]}", $"MidiCC{i}", MappingCategory.Triggers, $"MidiCC{i}Neg"));

            // Note outputs — each note is a button (Note On/Off).
            for (int i = 0; i < mc.NoteCount; i++)
                Mappings.Add(new MappingItem($"Note {noteNumbers[i]}", $"MidiNote{i}", MappingCategory.Buttons));
        }

        /// <summary>
        /// Keyboard + Mouse mappings — full keyboard keys, mouse buttons, and mouse axes.
        /// Targets use "Kbm" prefix for dictionary-based PadSetting storage.
        /// Key targets: "KbmKey{vk}" where vk is the Windows virtual-key code (hex).
        /// Mouse buttons: "KbmMBtn{0-4}" (LMB, RMB, MMB, X1, X2).
        /// Mouse axes: "KbmMouseX"/"KbmMouseY" (bidirectional), "KbmScroll" (bidirectional).
        /// </summary>
        private void InitializeKeyboardMouseMappings()
        {
            // Helper to add a keyboard key mapping target
            void AddKey(string label, byte vk)
                => Mappings.Add(new MappingItem(label, $"KbmKey{vk:X2}", MappingCategory.Buttons));

            // ── Letters ──
            for (int i = 0; i < 26; i++)
                AddKey(((char)('A' + i)).ToString(), (byte)(0x41 + i));

            // ── Numbers ──
            for (int i = 0; i <= 9; i++)
                AddKey(i.ToString(), (byte)(0x30 + i));

            // ── Function keys ──
            for (int i = 1; i <= 12; i++)
                AddKey($"F{i}", (byte)(0x6F + i)); // VK_F1=0x70 .. VK_F12=0x7B

            // ── Modifiers ──
            AddKey(Strings.Instance.Key_LeftShift, 0xA0);
            AddKey(Strings.Instance.Key_RightShift, 0xA1);
            AddKey(Strings.Instance.Key_LeftCtrl, 0xA2);
            AddKey(Strings.Instance.Key_RightCtrl, 0xA3);
            AddKey(Strings.Instance.Key_LeftAlt, 0xA4);
            AddKey(Strings.Instance.Key_RightAlt, 0xA5);

            // ── Special keys ──
            AddKey(Strings.Instance.Key_Space, 0x20);
            AddKey(Strings.Instance.Key_Enter, 0x0D);
            AddKey(Strings.Instance.Key_Escape, 0x1B);
            AddKey(Strings.Instance.Key_Tab, 0x09);
            AddKey(Strings.Instance.Key_Backspace, 0x08);
            AddKey(Strings.Instance.Key_CapsLock, 0x14);
            AddKey(Strings.Instance.Key_NumLock, 0x90);
            AddKey(Strings.Instance.Key_ScrollLock, 0x91);
            AddKey(Strings.Instance.Key_PrintScreen, 0x2C);
            AddKey(Strings.Instance.Key_Pause, 0x13);

            // ── Navigation ──
            AddKey(Strings.Instance.Key_Up, 0x26);
            AddKey(Strings.Instance.Key_Down, 0x28);
            AddKey(Strings.Instance.Key_Left, 0x25);
            AddKey(Strings.Instance.Key_Right, 0x27);
            AddKey(Strings.Instance.Key_Home, 0x24);
            AddKey(Strings.Instance.Key_End, 0x23);
            AddKey(Strings.Instance.Key_PageUp, 0x21);
            AddKey(Strings.Instance.Key_PageDown, 0x22);
            AddKey(Strings.Instance.Key_Insert, 0x2D);
            AddKey(Strings.Instance.Key_Delete, 0x2E);

            // ── Punctuation ──
            AddKey(";", 0xBA);
            AddKey("=", 0xBB);
            AddKey(",", 0xBC);
            AddKey("-", 0xBD);
            AddKey(".", 0xBE);
            AddKey("/", 0xBF);
            AddKey("`", 0xC0);
            AddKey("[", 0xDB);
            AddKey("\\", 0xDC);
            AddKey("]", 0xDD);
            AddKey("'", 0xDE);

            // ── Numpad ──
            for (int i = 0; i <= 9; i++)
                AddKey($"Num {i}", (byte)(0x60 + i));
            AddKey("Num *", 0x6A);
            AddKey("Num +", 0x6B);
            AddKey("Num -", 0x6D);
            AddKey("Num .", 0x6E);
            AddKey("Num /", 0x6F);

            // ── Mouse buttons ──
            Mappings.Add(new MappingItem(Strings.Instance.Mouse_LeftClick, "KbmMBtn0", MappingCategory.Buttons));
            Mappings.Add(new MappingItem(Strings.Instance.Mouse_RightClick, "KbmMBtn1", MappingCategory.Buttons));
            Mappings.Add(new MappingItem(Strings.Instance.Mouse_MiddleClick, "KbmMBtn2", MappingCategory.Buttons));
            Mappings.Add(new MappingItem(Strings.Instance.Mouse_Button4, "KbmMBtn3", MappingCategory.Buttons));
            Mappings.Add(new MappingItem(Strings.Instance.Mouse_Button5, "KbmMBtn4", MappingCategory.Buttons));

            // ── Mouse movement axes (bidirectional) ──
            Mappings.Add(new MappingItem(Strings.Instance.Mouse_X, "KbmMouseX", MappingCategory.LeftStick, negSettingName: "KbmMouseXNeg"));
            Mappings.Add(new MappingItem(Strings.Instance.Mouse_Y, "KbmMouseY", MappingCategory.LeftStick, negSettingName: "KbmMouseYNeg"));

            // ── Mouse scroll (bidirectional, visualized as Right Stick Y) ──
            Mappings.Add(new MappingItem(Strings.Instance.Mouse_Scroll, "KbmScroll", MappingCategory.RightStick, negSettingName: "KbmScrollNeg"));

            // ── Horizontal mouse scroll (issue #154, office-mouse tilt wheel) ──
            Mappings.Add(new MappingItem(Strings.Instance.Mouse_ScrollH, "KbmScrollH", MappingCategory.RightStick, negSettingName: "KbmScrollHNeg"));
        }

        /// <summary>
        /// Dynamic Extended Custom mappings — numbered buttons, sticks, triggers, POVs.
        /// Axis layout interleaves sticks and triggers: [Stick0 X,Y | Trig0 | Stick1 X,Y | Trig1 | ...].
        /// </summary>
        private void InitializeExtendedCustomMappings()
        {
            var cfg = ExtendedConfig;
            int stickCount = cfg.ThumbstickCount;
            int triggerCount = cfg.TriggerCount;

            cfg.ComputeAxisLayout(out var stickAxisX, out var stickAxisY, out var triggerAxis);

            // Stick axes (paired)
            for (int i = 0; i < stickCount; i++)
            {
                var cat = i == 0 ? MappingCategory.LeftStick : MappingCategory.RightStick;
                Mappings.Add(new MappingItem(string.Format(Strings.Instance.Extended_Stick_Format, i + 1), $"ExtendedAxis{stickAxisX[i]}", cat, $"ExtendedAxis{stickAxisX[i]}Neg"));
                Mappings.Add(new MappingItem(string.Format(Strings.Instance.Extended_StickY_Format, i + 1), $"ExtendedAxis{stickAxisY[i]}", cat, $"ExtendedAxis{stickAxisY[i]}Neg"));
            }

            // Trigger axes (unpaired)
            for (int i = 0; i < triggerCount; i++)
                Mappings.Add(new MappingItem(string.Format(Strings.Instance.Extended_Trigger_Format, i + 1), $"ExtendedAxis{triggerAxis[i]}", MappingCategory.Triggers));

            // Buttons. Switch Pro profiles letter each raw index in the
            // Nintendo convention (#215) through the same MacroButtonNames
            // seam the macro / menu pickers read; other profiles keep the
            // numbered labels.
            for (int i = 0; i < cfg.ButtonCount; i++)
                Mappings.Add(new MappingItem(MacroButtonNames.ExtendedButtonLabel(ProfileId, i + 1), $"ExtendedBtn{i}", MappingCategory.Buttons));

            // POVs
            for (int i = 0; i < cfg.PovCount; i++)
            {
                string label = cfg.PovCount == 1 ? Strings.Instance.Extended_DPad : string.Format(Strings.Instance.Extended_POV_Format, i + 1);
                Mappings.Add(new MappingItem($"{label} Up", $"ExtendedPov{i}Up", MappingCategory.DPad));
                Mappings.Add(new MappingItem($"{label} Down", $"ExtendedPov{i}Down", MappingCategory.DPad));
                Mappings.Add(new MappingItem($"{label} Left", $"ExtendedPov{i}Left", MappingCategory.DPad));
                Mappings.Add(new MappingItem($"{label} Right", $"ExtendedPov{i}Right", MappingCategory.DPad));
            }
        }

        // ═══════════════════════════════════════════════
        //  Force feedback — unchanged
        // ═══════════════════════════════════════════════

        // ═══════════════════════════════════════════════
        //  Gyro tuning (per-device, per-slot) — v3.3 SteamInput parity
        // ═══════════════════════════════════════════════

        private double _gyroSensitivityH = 1.0;
        public double GyroSensitivityH
        {
            get => _gyroSensitivityH;
            set
            {
                if (SetProperty(ref _gyroSensitivityH, Math.Clamp(value, 0.1, 10)))
                {
                    OnPropertyChanged(nameof(GyroSensitivityH_DegPerTurn));
                }
            }
        }

        private double _gyroSensitivityV = 1.0;
        public double GyroSensitivityV
        {
            get => _gyroSensitivityV;
            set
            {
                if (SetProperty(ref _gyroSensitivityV, Math.Clamp(value, 0.1, 10)))
                {
                    OnPropertyChanged(nameof(GyroSensitivityV_DegPerTurn));
                }
            }
        }

        /// <summary>Real-world readout: degrees of physical rotation per
        /// one full screen turn at the current H sensitivity. Steam-style
        /// reference; 360° baseline at multiplier 1.0.</summary>
        public double GyroSensitivityH_DegPerTurn
            => _gyroSensitivityH > 0 ? 360.0 / _gyroSensitivityH : 0;

        public double GyroSensitivityV_DegPerTurn
            => _gyroSensitivityV > 0 ? 360.0 / _gyroSensitivityV : 0;

        private double _gyroDeadZoneDegPerSec = 3.0;
        public double GyroDeadZoneDegPerSec
        {
            get => _gyroDeadZoneDegPerSec;
            set => SetProperty(ref _gyroDeadZoneDegPerSec, Math.Clamp(value, 0, 30));
        }

        private double _gyroSmoothingAlpha;
        public double GyroSmoothingAlpha
        {
            get => _gyroSmoothingAlpha;
            set => SetProperty(ref _gyroSmoothingAlpha, Math.Clamp(value, 0, 0.95));
        }

        private double _gyroAcceleration;
        public double GyroAcceleration
        {
            get => _gyroAcceleration;
            set => SetProperty(ref _gyroAcceleration, Math.Clamp(value, 0, 2));
        }

        private string _gyroOutputCurve = "Linear";
        public string GyroOutputCurve
        {
            get => _gyroOutputCurve;
            set => SetProperty(ref _gyroOutputCurve, value ?? "Linear");
        }

        public System.Collections.Generic.IReadOnlyList<GyroLabeledOption> GyroOutputCurveOptions { get; } = new[]
        {
            new GyroLabeledOption(() => Strings.Instance.Pad_Gyro_Curve_Linear,     "Linear"),
            new GyroLabeledOption(() => Strings.Instance.Pad_Gyro_Curve_Aggressive, "Aggressive"),
            new GyroLabeledOption(() => Strings.Instance.Pad_Gyro_Curve_Relaxed,    "Relaxed"),
            new GyroLabeledOption(() => Strings.Instance.Pad_Gyro_Curve_Wide,       "Wide"),
            new GyroLabeledOption(() => Strings.Instance.Pad_Gyro_Curve_ExtraWide,  "ExtraWide"),
        };

        private string _gyroSensitivityUnits = "Multiplier";
        public string GyroSensitivityUnits
        {
            get => _gyroSensitivityUnits;
            set => SetProperty(ref _gyroSensitivityUnits, value ?? "Multiplier");
        }

        public System.Collections.Generic.IReadOnlyList<GyroLabeledOption> GyroSensitivityUnitsOptions { get; } = new[]
        {
            new GyroLabeledOption(() => Strings.Instance.Pad_Gyro_Units_Multiplier,        "Multiplier"),
            new GyroLabeledOption(() => Strings.Instance.Pad_Gyro_Units_DegPerScreenTurn,  "DegPerScreenTurn"),
        };

        private double _gyroEasyAimStickThreshold;
        /// <summary>Right-stick deflection threshold (0-100%) below which
        /// gyro output is zeroed. 0 = always on (default).</summary>
        public double GyroEasyAimStickThreshold
        {
            get => _gyroEasyAimStickThreshold;
            set => SetProperty(ref _gyroEasyAimStickThreshold, Math.Clamp(value, 0, 100));
        }

        // ─── JoyShockMapper-canonextensions ───────────────────────

        private string _gyroSpace = "Local";
        public string GyroSpace
        {
            get => _gyroSpace;
            set => SetProperty(ref _gyroSpace, string.IsNullOrEmpty(value) ? "Local" : value);
        }
        /// <summary>Localized-display + stored-value pairs for the gyro
        /// space dropdown. Stored value stays as the canonical English
        /// identifier so PadForge.xml round-trips are stable across
        /// locale changes; display text is read from the strings resx.</summary>
        public IReadOnlyList<GyroLabeledOption> GyroSpaceOptions { get; } = new[]
        {
            new GyroLabeledOption(() => Strings.Instance.Pad_Gyro_Space_Local,  "Local"),
            new GyroLabeledOption(() => Strings.Instance.Pad_Gyro_Space_Player, "Player"),
            new GyroLabeledOption(() => Strings.Instance.Pad_Gyro_Space_World,  "World"),
        };

        // ── Motion Steering (v3.4 #94) — SETTINGS for the "Motion Lean" input.
        // Motion Lean is a first-class input descriptor: the user maps it to an
        // axis from the input dropdown in Mappings, like any gyro input. This
        // card never targets or overrides an axis — the earlier design (an
        // Enable + "Steers" target that stamped MotionLeanX over the chosen
        // stick axis's existing source) replaced one input with another, which
        // no other PadForge feature does, and was removed for exactly that
        // reason. What remains is per-(slot, device) tuning: tilt deadzones and
        // grip orientation, pushed onto the device's Motion Lean sources at
        // save time (SettingsService.ApplyMotionLeanParamsToRow). ──

        /// <summary>The stick X axes this slot's output exposes, as (label, X target, Y target).
        /// Per-stick steering (Winding / Angle) resolves its target against this
        /// one list, so a standard gamepad (Left/Right) and an Extended custom layout (one entry
        /// per thumbstick, using the same interleaved axis layout the rest of the Extended
        /// pipeline uses) go through the same code.</summary>
        public IReadOnlyList<(string Label, string XTarget, string YTarget)> GetSteerableSticks()
        {
            var list = new List<(string, string, string)>();
            if (OutputType == VirtualControllerType.Extended)
            {
                ExtendedConfig.ComputeAxisLayout(out var sx, out var sy, out _);
                for (int g = 0; g < sx.Length && g < sy.Length; g++)
                {
                    string label = (g < StickConfigs.Count ? StickConfigs[g].Title : null)
                        ?? string.Format(Strings.Instance.Extended_Stick_Format, g + 1);
                    list.Add((label, $"ExtendedAxis{sx[g]}", $"ExtendedAxis{sy[g]}"));
                }
            }
            else
            {
                list.Add((Strings.Instance.Btn_LeftStickX,  "LeftThumbAxisX",  "LeftThumbAxisY"));
                list.Add((Strings.Instance.Btn_RightStickX, "RightThumbAxisX", "RightThumbAxisY"));
            }
            return list;
        }

        private double _motionSteerInnerDz = 15;
        public double MotionSteerInnerDz { get => _motionSteerInnerDz; set => SetProperty(ref _motionSteerInnerDz, Math.Clamp(value, 0, 179)); }
        private double _motionSteerOuterDz = 135;
        public double MotionSteerOuterDz { get => _motionSteerOuterDz; set => SetProperty(ref _motionSteerOuterDz, Math.Clamp(value, 0, 179)); }

        private static readonly string[] MotionSteerOrientValues = { "Forward", "Left", "Right", "Backward" };
        private int _motionSteerOrientIndex;
        public int MotionSteerOrientIndex
        {
            get => _motionSteerOrientIndex;
            set { if (SetProperty(ref _motionSteerOrientIndex, Math.Clamp(value, 0, 3))) OnPropertyChanged(nameof(MotionSteerOrient)); }
        }
        public string MotionSteerOrient => MotionSteerOrientValues[Math.Clamp(_motionSteerOrientIndex, 0, 3)];
        public void SetMotionSteerOrient(string o)
        {
            int i = System.Array.IndexOf(MotionSteerOrientValues, o ?? "Forward");
            MotionSteerOrientIndex = i >= 0 ? i : 0;
        }

        private RelayCommand _resetMotionSteerInnerCommand, _resetMotionSteerOuterCommand,
            _resetMotionSteerOrientCommand, _resetMotionSteerAllCommand;
        public RelayCommand ResetMotionSteerInnerCommand => _resetMotionSteerInnerCommand ??= new RelayCommand(() => MotionSteerInnerDz = 15);
        public RelayCommand ResetMotionSteerOuterCommand => _resetMotionSteerOuterCommand ??= new RelayCommand(() => MotionSteerOuterDz = 135);
        public RelayCommand ResetMotionSteerOrientCommand => _resetMotionSteerOrientCommand ??= new RelayCommand(() => MotionSteerOrientIndex = 0);
        public RelayCommand ResetMotionSteerAllCommand => _resetMotionSteerAllCommand ??= new RelayCommand(() =>
        {
            MotionSteerInnerDz = 15; MotionSteerOuterDz = 135; MotionSteerOrientIndex = 0;
        });

        // ── Flick Stick tuning (#225, wave 4a) ──
        // Per-(slot, device) lens over the "Flick Stick ..." sources in this
        // slot's KBM mapping set: the save pipeline stamps these onto every
        // flick source (ApplyFlickStickParamsToRow, the Motion Steering push
        // pattern) and persists them in the PadSetting extended-mapping bag
        // ("FlickStick*" keys). Defaults mirror the MappingSource ParamFlick*
        // defaults (JSM-grounded; see MappingSource.cs).
        private double _flickCountsPer360 = 14400;
        public double FlickCountsPer360 { get => _flickCountsPer360; set => SetProperty(ref _flickCountsPer360, Math.Clamp(value, 100, 100000)); }
        private double _flickTime = 0.1;
        public double FlickTime { get => _flickTime; set => SetProperty(ref _flickTime, Math.Clamp(value, 0.01, 2.0)); }
        private double _flickThreshold = 0.9;
        public double FlickThreshold { get => _flickThreshold; set => SetProperty(ref _flickThreshold, Math.Clamp(value, 0.1, 1.0)); }
        private double _flickSnapStrength = 1.0;
        public double FlickSnapStrength { get => _flickSnapStrength; set => SetProperty(ref _flickSnapStrength, Math.Clamp(value, 0.0, 1.0)); }
        private double _flickForwardDeadzone;
        public double FlickForwardDeadzone { get => _flickForwardDeadzone; set => SetProperty(ref _flickForwardDeadzone, Math.Clamp(value, 0.0, 180.0)); }
        private double _flickSmoothing = -1;
        public double FlickSmoothing { get => _flickSmoothing; set => SetProperty(ref _flickSmoothing, Math.Clamp(value, -1.0, 0.5)); }
        private bool _flickOnEngage;
        public bool FlickOnEngage { get => _flickOnEngage; set => SetProperty(ref _flickOnEngage, value); }

        // Snap mode as a Tag-backed string (the GyroEngageStickSide combo
        // pattern), value set = SourceKindRuntime.FlickSnapIntervalRad's.
        private string _flickSnapMode = "None";
        public string FlickSnapMode
        {
            get => _flickSnapMode;
            set => SetProperty(ref _flickSnapMode, string.IsNullOrEmpty(value) ? "None" : value);
        }

        private RelayCommand _resetFlickCountsPer360Command, _resetFlickTimeCommand,
            _resetFlickThresholdCommand, _resetFlickSnapModeCommand, _resetFlickSnapStrengthCommand,
            _resetFlickForwardDeadzoneCommand, _resetFlickSmoothingCommand, _resetFlickOnEngageCommand,
            _resetFlickStickCardCommand;
        public RelayCommand ResetFlickCountsPer360Command => _resetFlickCountsPer360Command ??= new RelayCommand(() => FlickCountsPer360 = 14400);
        public RelayCommand ResetFlickTimeCommand => _resetFlickTimeCommand ??= new RelayCommand(() => FlickTime = 0.1);
        public RelayCommand ResetFlickThresholdCommand => _resetFlickThresholdCommand ??= new RelayCommand(() => FlickThreshold = 0.9);
        public RelayCommand ResetFlickSnapModeCommand => _resetFlickSnapModeCommand ??= new RelayCommand(() => FlickSnapMode = "None");
        public RelayCommand ResetFlickSnapStrengthCommand => _resetFlickSnapStrengthCommand ??= new RelayCommand(() => FlickSnapStrength = 1.0);
        public RelayCommand ResetFlickForwardDeadzoneCommand => _resetFlickForwardDeadzoneCommand ??= new RelayCommand(() => FlickForwardDeadzone = 0);
        public RelayCommand ResetFlickSmoothingCommand => _resetFlickSmoothingCommand ??= new RelayCommand(() => FlickSmoothing = -1);
        public RelayCommand ResetFlickOnEngageCommand => _resetFlickOnEngageCommand ??= new RelayCommand(() => FlickOnEngage = false);
        public RelayCommand ResetFlickStickCardCommand => _resetFlickStickCardCommand ??= new RelayCommand(() =>
        {
            FlickCountsPer360 = 14400; FlickTime = 0.1; FlickThreshold = 0.9;
            FlickSnapMode = "None"; FlickSnapStrength = 1.0; FlickForwardDeadzone = 0;
            FlickSmoothing = -1; FlickOnEngage = false;
        });

        private double _gyroPlayerSpaceYawRelaxFactor = 1.41;
        public double GyroPlayerSpaceYawRelaxFactor
        {
            get => _gyroPlayerSpaceYawRelaxFactor;
            set => SetProperty(ref _gyroPlayerSpaceYawRelaxFactor, Math.Clamp(value, 1.0, 2.0));
        }

        private double _gyroWorldSpaceSideReductionThreshold = 0.125;
        public double GyroWorldSpaceSideReductionThreshold
        {
            get => _gyroWorldSpaceSideReductionThreshold;
            set => SetProperty(ref _gyroWorldSpaceSideReductionThreshold, Math.Clamp(value, 0.0, 0.5));
        }

        private double _gyroTighteningThresholdDegPerSec = 3.0;
        public double GyroTighteningThresholdDegPerSec
        {
            get => _gyroTighteningThresholdDegPerSec;
            set => SetProperty(ref _gyroTighteningThresholdDegPerSec, Math.Clamp(value, 0, 30));
        }

        private double _gyroSmoothingThresholdDegPerSec = 8.0;
        public double GyroSmoothingThresholdDegPerSec
        {
            get => _gyroSmoothingThresholdDegPerSec;
            set => SetProperty(ref _gyroSmoothingThresholdDegPerSec, Math.Clamp(value, 0, 30));
        }

        private double _gyroSmoothingWindowMs = 50;
        public double GyroSmoothingWindowMs
        {
            get => _gyroSmoothingWindowMs;
            set => SetProperty(ref _gyroSmoothingWindowMs, Math.Clamp(value, 10, 200));
        }

        private double _gyroRealWorldCalibration;
        public double GyroRealWorldCalibration
        {
            get => _gyroRealWorldCalibration;
            set => SetProperty(ref _gyroRealWorldCalibration, Math.Clamp(value, 0, 2));
        }

        private string _gyroAimEngageButton = "";
        public string GyroAimEngageButton
        {
            get => _gyroAimEngageButton;
            set
            {
                if (SetProperty(ref _gyroAimEngageButton, value ?? ""))
                    OnPropertyChanged(nameof(GyroAimEngageSelectedInput));
            }
        }

        private string _gyroAimEngageDeviceGuid = "";
        public string GyroAimEngageDeviceGuid
        {
            get => _gyroAimEngageDeviceGuid;
            set
            {
                if (SetProperty(ref _gyroAimEngageDeviceGuid, value ?? ""))
                    OnPropertyChanged(nameof(GyroAimEngageSelectedInput));
            }
        }

        private string _gyroAimEngageMode = "Hold";
        /// <summary>"Hold" (default) — gyro fires while the engage button
        /// is held. "Toggle" — each rising edge flips a sticky per-slot
        /// engaged bit. OR-combined with the SetGyroEngaged macro action's
        /// bit at the gyro evaluator. Per-(device, slot) like the rest of
        /// the gyro tuning, but the runtime state itself is per-slot
        /// volatile and resets on profile switch.</summary>
        public string GyroAimEngageMode
        {
            get => _gyroAimEngageMode;
            set => SetProperty(ref _gyroAimEngageMode, string.IsNullOrEmpty(value) ? "Hold" : value);
        }

        private string _gyroEngageStickSide = "Right";
        /// <summary>Which stick's deflection drives the Easy-Aim threshold
        /// gate (issue #120): "Right" (default), "Left", or "Either" (the
        /// larger of the two). Empty collapses to "Right" so old profiles
        /// keep their original right-stick behavior.</summary>
        public string GyroEngageStickSide
        {
            get => _gyroEngageStickSide;
            set => SetProperty(ref _gyroEngageStickSide, string.IsNullOrEmpty(value) ? "Right" : value);
        }

        private string _gyroEngageStickDirection = "Full";
        /// <summary>Which component of the engage stick(s) drives the
        /// Easy-Aim threshold gate (issue #120): "Full" (default, radial),
        /// "X"/"Y", or "XNeg"/"XPos"/"YNeg"/"YPos". Empty collapses to
        /// "Full" so old profiles keep their original radial behavior.</summary>
        public string GyroEngageStickDirection
        {
            get => _gyroEngageStickDirection;
            set => SetProperty(ref _gyroEngageStickDirection, string.IsNullOrEmpty(value) ? "Full" : value);
        }

        /// <summary>Tells the view to re-resolve
        /// <see cref="GyroAimEngageSelectedInput"/> after
        /// <see cref="SlotAvailableInputs"/> is populated. Called by
        /// InputService.PopulateAvailableInputs after rebuilding the
        /// slot's cross-device input list — without it, the picker
        /// stays empty until the user reselects.</summary>
        public void OnGyroAimEngageSelectedInputRefresh()
            => OnPropertyChanged(nameof(GyroAimEngageSelectedInput));

        /// <summary>InputChoice projection over the
        /// <see cref="GyroAimEngageButton"/> + <see cref="GyroAimEngageDeviceGuid"/>
        /// pair. Getter resolves the matching entry in
        /// <see cref="SlotAvailableInputs"/>; setter writes both
        /// backing strings atomically. Returning null collapses the
        /// ComboBox to its placeholder. Wired by the Aim Engage
        /// cross-device picker on the Gyro tab.</summary>
        public InputChoice GyroAimEngageSelectedInput
        {
            get
            {
                if (string.IsNullOrEmpty(_gyroAimEngageButton)) return null;
                foreach (var c in SlotAvailableInputs)
                {
                    if (c == null) continue;
                    if (string.Equals(c.Descriptor, _gyroAimEngageButton, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(c.DeviceGuid ?? "", _gyroAimEngageDeviceGuid ?? "", StringComparison.OrdinalIgnoreCase))
                        return c;
                }
                return null;
            }
            set
            {
                // Ignore a null write-back from the ComboBox's TwoWay
                // binding. The picker's SelectedItem goes to null any time
                // the getter can't find the current (descriptor, deviceGuid)
                // in SlotAvailableInputs — which happens whenever
                // LoadPadSettingToViewModel runs before PopulateAvailableInputs
                // has rebuilt the list for the newly-selected device.
                // Treating that transient null as "the user picked nothing"
                // silently wipes the saved Aim Engage binding on every
                // device switch / restart. The Reset button (Pad page
                // convention) is the only sanctioned clear path.
                if (value == null) return;
                GyroAimEngageButton = value.Descriptor ?? "";
                GyroAimEngageDeviceGuid = value.DeviceGuid ?? "";
            }
        }

        private bool _gyroInvertPitch;
        public bool GyroInvertPitch
        {
            get => _gyroInvertPitch;
            set => SetProperty(ref _gyroInvertPitch, value);
        }

        private bool _gyroInvertYawRoll;
        public bool GyroInvertYawRoll
        {
            get => _gyroInvertYawRoll;
            set => SetProperty(ref _gyroInvertYawRoll, value);
        }

        // Default false: a user expects a clean passthrough — the
        // virtual controller hands the game the raw sensor reading.
        // Checking it routes the Gyro tab's tuning (deadzone, sens,
        // smoothing, response curve, invert) through the passthrough
        // too, in addition to gyro-as-mapping-source reads.
        private bool _gyroApplyTuningToPassthrough = false;
        public bool GyroApplyTuningToPassthrough
        {
            get => _gyroApplyTuningToPassthrough;
            set => SetProperty(ref _gyroApplyTuningToPassthrough, value);
        }

        // Live calibrated rate readouts (deg/s) — refreshed by InputService
        // when a Pad page is visible on a slot with a gyro-capable device.
        private double _gyroLiveRatePitch;
        public double GyroLiveRatePitch { get => _gyroLiveRatePitch; set => SetProperty(ref _gyroLiveRatePitch, value); }
        private double _gyroLiveRateYaw;
        public double GyroLiveRateYaw   { get => _gyroLiveRateYaw;   set => SetProperty(ref _gyroLiveRateYaw,   value); }
        private double _gyroLiveRateRoll;
        public double GyroLiveRateRoll  { get => _gyroLiveRateRoll;  set => SetProperty(ref _gyroLiveRateRoll,  value); }

        // Live accelerometer readout (g-units) — refreshed alongside the
        // gyro live rates above. No bias subtraction (no accel tuning
        // chain exists in PadForge); the value reflects the raw sensor
        // reading scaled to g-units so the user can verify the
        // controller is oriented as they expect.
        private double _accelLiveX;
        public double AccelLiveX { get => _accelLiveX; set => SetProperty(ref _accelLiveX, value); }
        private double _accelLiveY;
        public double AccelLiveY { get => _accelLiveY; set => SetProperty(ref _accelLiveY, value); }
        private double _accelLiveZ;
        public double AccelLiveZ { get => _accelLiveZ; set => SetProperty(ref _accelLiveZ, value); }

        private string _gyroCalibrationLabel = "";
        public string GyroCalibrationLabel
        {
            get => _gyroCalibrationLabel;
            set => SetProperty(ref _gyroCalibrationLabel, value);
        }

        /// <summary>Raised when the user clicks Calibrate Gyro on the
        /// Pad page's Gyro tab. MainWindow wires the handler to
        /// InputService.GyroCalibrator.RecalibrateAsync against the
        /// slot's selected mapped device.</summary>
        public event EventHandler GyroCalibrateRequested;
        public void FireGyroCalibrate() => GyroCalibrateRequested?.Invoke(this, EventArgs.Empty);

        public event EventHandler GyroResetCalibrationRequested;
        public void FireGyroResetCalibration() => GyroResetCalibrationRequested?.Invoke(this, EventArgs.Empty);

        private RelayCommand _gyroCalibrateCommand;
        public RelayCommand GyroCalibrateCommand
            => _gyroCalibrateCommand ??= new RelayCommand(FireGyroCalibrate);

        private RelayCommand _gyroResetCalibrationCommand;
        public RelayCommand GyroResetCalibrationCommand
            => _gyroResetCalibrationCommand ??= new RelayCommand(FireGyroResetCalibration);

        // ─── Per-row reset commands. Each restores ONE field to its
        //     default so the user can revert a single tweak without
        //     clobbering the rest of the gyro tuning. Matches the
        //     per-control reset-button convention used across the
        //     Triggers / AT / Lighting tabs. ───
        private RelayCommand _resetGyroSpaceCommand;
        public RelayCommand ResetGyroSpaceCommand =>
            _resetGyroSpaceCommand ??= new RelayCommand(() => GyroSpace = "Local");

        private RelayCommand _resetGyroSensitivityHCommand;
        public RelayCommand ResetGyroSensitivityHCommand =>
            _resetGyroSensitivityHCommand ??= new RelayCommand(() => GyroSensitivityH = 1.0);

        private RelayCommand _resetGyroSensitivityVCommand;
        public RelayCommand ResetGyroSensitivityVCommand =>
            _resetGyroSensitivityVCommand ??= new RelayCommand(() => GyroSensitivityV = 1.0);

        private RelayCommand _resetGyroSensitivityUnitsCommand;
        public RelayCommand ResetGyroSensitivityUnitsCommand =>
            _resetGyroSensitivityUnitsCommand ??= new RelayCommand(() => GyroSensitivityUnits = "Multiplier");

        private RelayCommand _resetGyroInvertPitchCommand;
        public RelayCommand ResetGyroInvertPitchCommand =>
            _resetGyroInvertPitchCommand ??= new RelayCommand(() => GyroInvertPitch = false);

        private RelayCommand _resetGyroInvertYawRollCommand;
        public RelayCommand ResetGyroInvertYawRollCommand =>
            _resetGyroInvertYawRollCommand ??= new RelayCommand(() => GyroInvertYawRoll = false);

        private RelayCommand _resetGyroApplyToPassthroughCommand;
        public RelayCommand ResetGyroApplyToPassthroughCommand =>
            _resetGyroApplyToPassthroughCommand ??= new RelayCommand(() => GyroApplyTuningToPassthrough = false);

        private RelayCommand _resetGyroRealWorldCalibrationCommand;
        public RelayCommand ResetGyroRealWorldCalibrationCommand =>
            _resetGyroRealWorldCalibrationCommand ??= new RelayCommand(() => GyroRealWorldCalibration = 0);

        private RelayCommand _resetGyroDeadZoneCommand;
        public RelayCommand ResetGyroDeadZoneCommand =>
            _resetGyroDeadZoneCommand ??= new RelayCommand(() => GyroDeadZoneDegPerSec = 3.0);

        private RelayCommand _resetGyroTighteningCommand;
        public RelayCommand ResetGyroTighteningCommand =>
            _resetGyroTighteningCommand ??= new RelayCommand(() => GyroTighteningThresholdDegPerSec = 3.0);

        private RelayCommand _resetGyroSmoothingThresholdCommand;
        public RelayCommand ResetGyroSmoothingThresholdCommand =>
            _resetGyroSmoothingThresholdCommand ??= new RelayCommand(() => GyroSmoothingThresholdDegPerSec = 8.0);

        private RelayCommand _resetGyroSmoothingWindowCommand;
        public RelayCommand ResetGyroSmoothingWindowCommand =>
            _resetGyroSmoothingWindowCommand ??= new RelayCommand(() => GyroSmoothingWindowMs = 50);

        private RelayCommand _resetGyroAccelerationCommand;
        public RelayCommand ResetGyroAccelerationCommand =>
            _resetGyroAccelerationCommand ??= new RelayCommand(() => GyroAcceleration = 0);

        private RelayCommand _resetGyroOutputCurveCommand;
        public RelayCommand ResetGyroOutputCurveCommand =>
            _resetGyroOutputCurveCommand ??= new RelayCommand(() => GyroOutputCurve = "Linear");

        private RelayCommand _resetGyroEasyAimStickThresholdCommand;
        public RelayCommand ResetGyroEasyAimStickThresholdCommand =>
            _resetGyroEasyAimStickThresholdCommand ??= new RelayCommand(() => GyroEasyAimStickThreshold = 0);

        private RelayCommand _resetGyroAimEngageButtonCommand;
        public RelayCommand ResetGyroAimEngageButtonCommand =>
            _resetGyroAimEngageButtonCommand ??= new RelayCommand(() =>
            {
                GyroAimEngageButton = "";
                GyroAimEngageDeviceGuid = "";
            });

        private RelayCommand _resetGyroAimEngageModeCommand;
        public RelayCommand ResetGyroAimEngageModeCommand =>
            _resetGyroAimEngageModeCommand ??= new RelayCommand(() => GyroAimEngageMode = "Hold");

        private RelayCommand _resetGyroEngageStickSideCommand;
        public RelayCommand ResetGyroEngageStickSideCommand =>
            _resetGyroEngageStickSideCommand ??= new RelayCommand(() => GyroEngageStickSide = "Right");

        private RelayCommand _resetGyroEngageStickDirectionCommand;
        public RelayCommand ResetGyroEngageStickDirectionCommand =>
            _resetGyroEngageStickDirectionCommand ??= new RelayCommand(() => GyroEngageStickDirection = "Full");

        /// <summary>Whether the Aim Engage recorder is currently
        /// listening for the next physical input. Drives the record
        /// button's icon + tooltip swap so the affordance matches the
        /// mapping-table convention (Stop glyph + "Recording…" tooltip
        /// while active, Record glyph + "Record" tooltip while idle).</summary>
        private bool _gyroAimEngageRecording;
        public bool GyroAimEngageRecording
        {
            get => _gyroAimEngageRecording;
            set
            {
                if (SetProperty(ref _gyroAimEngageRecording, value))
                {
                    OnPropertyChanged(nameof(GyroAimEngageRecordButtonIcon));
                    OnPropertyChanged(nameof(GyroAimEngageRecordButtonText));
                }
            }
        }
        /// <summary>Segoe MDL2 glyph for the record button — Stop while
        /// recording, Record while idle. Mirrors
        /// <see cref="MappingItem.RecordButtonIcon"/>.</summary>
        // Note: the property below uses  (Stop) and  (Record)
        // via the implicit literal — keep the body single-line so future
        // edits don't desync the glyphs.
        public string GyroAimEngageRecordButtonIcon => _gyroAimEngageRecording ? "" : "";
        /// <summary>Localized tooltip for the record button. Mirrors
        /// <see cref="MappingItem.RecordButtonText"/>.</summary>
        public string GyroAimEngageRecordButtonText => _gyroAimEngageRecording
            ? Strings.Instance.Common_Recording
            : Strings.Instance.Common_Record;

        /// <summary>Fires when the user clicks the Record button next to
        /// the Aim Engage picker. MainWindow listens and either starts a
        /// freeform recorder session (if idle) or cancels (if already
        /// recording), matching the mapping-table Toggle pattern.</summary>
        public event EventHandler GyroAimEngageRecordRequested;
        public void FireGyroAimEngageRecord() => GyroAimEngageRecordRequested?.Invoke(this, EventArgs.Empty);

        private RelayCommand _gyroAimEngageRecordCommand;
        public RelayCommand GyroAimEngageRecordCommand =>
            _gyroAimEngageRecordCommand ??= new RelayCommand(FireGyroAimEngageRecord);

        // ─── Per-card Reset All commands. Each clears every field
        //     inside its card to the canonical default — matches the
        //     "Reset All" button next to each card title used in the
        //     Triggers, AT, Lighting, and Force Feedback tabs. ───
        private RelayCommand _resetGyroCalibrationCardCommand;
        public RelayCommand ResetGyroCalibrationCardCommand =>
            _resetGyroCalibrationCardCommand ??= new RelayCommand(FireGyroResetCalibration);

        private RelayCommand _resetGyroSensitivityCardCommand;
        public RelayCommand ResetGyroSensitivityCardCommand =>
            _resetGyroSensitivityCardCommand ??= new RelayCommand(() =>
            {
                GyroSpace = "Local";
                GyroSensitivityH = 1.0;
                GyroSensitivityV = 1.0;
                GyroSensitivityUnits = "Multiplier";
                GyroInvertPitch = false;
                GyroInvertYawRoll = false;
                GyroRealWorldCalibration = 0;
            });

        private RelayCommand _resetGyroResponseCardCommand;
        public RelayCommand ResetGyroResponseCardCommand =>
            _resetGyroResponseCardCommand ??= new RelayCommand(() =>
            {
                GyroDeadZoneDegPerSec = 3.0;
                GyroTighteningThresholdDegPerSec = 3.0;
                GyroSmoothingThresholdDegPerSec = 8.0;
                GyroSmoothingWindowMs = 50;
                GyroAcceleration = 0;
                GyroOutputCurve = "Linear";
            });

        private RelayCommand _resetGyroEngageCardCommand;
        public RelayCommand ResetGyroEngageCardCommand =>
            _resetGyroEngageCardCommand ??= new RelayCommand(() =>
            {
                GyroEasyAimStickThreshold = 0;
                GyroEngageStickSide = "Right";
                GyroEngageStickDirection = "Full";
                GyroAimEngageButton = "";
                GyroAimEngageDeviceGuid = "";
                GyroAimEngageMode = "Hold";
            });

        // ─────────────────────────────────────────────────────────────
        //  Trigger rumble routing (issue #102). Per-trigger Source / Mode /
        //  Scale plus a cross-device Activator (descriptor + owning device,
        //  the same picker shape as Gyro Aim Engage) and an Activator Mode
        //  (Hold / Toggle / AlwaysOn). Backed by the matching PadSetting
        //  fields, synced in LoadPadSettingToViewModel / SavePadSetting.
        // ─────────────────────────────────────────────────────────────

        // ─── Left trigger ───
        private string _leftTriggerRouteSource = "None";
        public string LeftTriggerRouteSource
        {
            get => _leftTriggerRouteSource;
            set => SetProperty(ref _leftTriggerRouteSource, string.IsNullOrEmpty(value) ? "None" : value);
        }

        private string _leftTriggerRouteMode = "Duplicate";
        public string LeftTriggerRouteMode
        {
            get => _leftTriggerRouteMode;
            set => SetProperty(ref _leftTriggerRouteMode, string.IsNullOrEmpty(value) ? "Duplicate" : value);
        }

        private int _leftTriggerRouteScale = 100;
        public int LeftTriggerRouteScale
        {
            get => _leftTriggerRouteScale;
            set => SetProperty(ref _leftTriggerRouteScale, Math.Clamp(value, 0, 200));
        }

        private string _leftTriggerRouteActivator = "";
        public string LeftTriggerRouteActivator
        {
            get => _leftTriggerRouteActivator;
            set { if (SetProperty(ref _leftTriggerRouteActivator, value ?? "")) OnPropertyChanged(nameof(LeftTriggerRouteActivatorSelectedInput)); }
        }

        private string _leftTriggerRouteActivatorDeviceGuid = "";
        public string LeftTriggerRouteActivatorDeviceGuid
        {
            get => _leftTriggerRouteActivatorDeviceGuid;
            set { if (SetProperty(ref _leftTriggerRouteActivatorDeviceGuid, value ?? "")) OnPropertyChanged(nameof(LeftTriggerRouteActivatorSelectedInput)); }
        }

        private string _leftTriggerRouteActivatorMode = "Hold";
        public string LeftTriggerRouteActivatorMode
        {
            get => _leftTriggerRouteActivatorMode;
            set => SetProperty(ref _leftTriggerRouteActivatorMode, string.IsNullOrEmpty(value) ? "Hold" : value);
        }

        /// <summary>InputChoice projection over the left-trigger activator
        /// descriptor + device GUID, mirroring <see cref="GyroAimEngageSelectedInput"/>.
        /// A null write-back from the ComboBox's TwoWay binding is ignored so a
        /// transient empty list (during device switch) doesn't wipe the binding.</summary>
        public InputChoice LeftTriggerRouteActivatorSelectedInput
        {
            get
            {
                if (string.IsNullOrEmpty(_leftTriggerRouteActivator)) return null;
                foreach (var c in SlotAvailableInputs)
                {
                    if (c == null) continue;
                    if (string.Equals(c.Descriptor, _leftTriggerRouteActivator, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(c.DeviceGuid ?? "", _leftTriggerRouteActivatorDeviceGuid ?? "", StringComparison.OrdinalIgnoreCase))
                        return c;
                }
                return null;
            }
            set
            {
                if (value == null) return;
                LeftTriggerRouteActivator = value.Descriptor ?? "";
                LeftTriggerRouteActivatorDeviceGuid = value.DeviceGuid ?? "";
            }
        }
        public void OnLeftTriggerRouteActivatorSelectedInputRefresh()
            => OnPropertyChanged(nameof(LeftTriggerRouteActivatorSelectedInput));

        private bool _leftTriggerRouteActivatorRecording;
        public bool LeftTriggerRouteActivatorRecording
        {
            get => _leftTriggerRouteActivatorRecording;
            set
            {
                if (SetProperty(ref _leftTriggerRouteActivatorRecording, value))
                {
                    OnPropertyChanged(nameof(LeftTriggerRouteActivatorRecordButtonIcon));
                    OnPropertyChanged(nameof(LeftTriggerRouteActivatorRecordButtonText));
                }
            }
        }
        // Segoe MDL2 glyphs:  Stop (recording),  Record (idle).
        public string LeftTriggerRouteActivatorRecordButtonIcon => _leftTriggerRouteActivatorRecording ? "" : "";
        public string LeftTriggerRouteActivatorRecordButtonText => _leftTriggerRouteActivatorRecording
            ? Strings.Instance.Common_Recording
            : Strings.Instance.Common_Record;
        public event EventHandler LeftTriggerRouteActivatorRecordRequested;
        public void FireLeftTriggerRouteActivatorRecord() => LeftTriggerRouteActivatorRecordRequested?.Invoke(this, EventArgs.Empty);
        private RelayCommand _leftTriggerRouteActivatorRecordCommand;
        public RelayCommand LeftTriggerRouteActivatorRecordCommand =>
            _leftTriggerRouteActivatorRecordCommand ??= new RelayCommand(FireLeftTriggerRouteActivatorRecord);

        private RelayCommand _resetLeftTriggerRouteActivatorCommand;
        public RelayCommand ResetLeftTriggerRouteActivatorCommand =>
            _resetLeftTriggerRouteActivatorCommand ??= new RelayCommand(() =>
            {
                LeftTriggerRouteActivator = "";
                LeftTriggerRouteActivatorDeviceGuid = "";
            });

        // ─── Right trigger ───
        private string _rightTriggerRouteSource = "None";
        public string RightTriggerRouteSource
        {
            get => _rightTriggerRouteSource;
            set => SetProperty(ref _rightTriggerRouteSource, string.IsNullOrEmpty(value) ? "None" : value);
        }

        private string _rightTriggerRouteMode = "Duplicate";
        public string RightTriggerRouteMode
        {
            get => _rightTriggerRouteMode;
            set => SetProperty(ref _rightTriggerRouteMode, string.IsNullOrEmpty(value) ? "Duplicate" : value);
        }

        private int _rightTriggerRouteScale = 100;
        public int RightTriggerRouteScale
        {
            get => _rightTriggerRouteScale;
            set => SetProperty(ref _rightTriggerRouteScale, Math.Clamp(value, 0, 200));
        }

        private string _rightTriggerRouteActivator = "";
        public string RightTriggerRouteActivator
        {
            get => _rightTriggerRouteActivator;
            set { if (SetProperty(ref _rightTriggerRouteActivator, value ?? "")) OnPropertyChanged(nameof(RightTriggerRouteActivatorSelectedInput)); }
        }

        private string _rightTriggerRouteActivatorDeviceGuid = "";
        public string RightTriggerRouteActivatorDeviceGuid
        {
            get => _rightTriggerRouteActivatorDeviceGuid;
            set { if (SetProperty(ref _rightTriggerRouteActivatorDeviceGuid, value ?? "")) OnPropertyChanged(nameof(RightTriggerRouteActivatorSelectedInput)); }
        }

        private string _rightTriggerRouteActivatorMode = "Hold";
        public string RightTriggerRouteActivatorMode
        {
            get => _rightTriggerRouteActivatorMode;
            set => SetProperty(ref _rightTriggerRouteActivatorMode, string.IsNullOrEmpty(value) ? "Hold" : value);
        }

        /// <summary>InputChoice projection over the right-trigger activator
        /// descriptor + device GUID. See <see cref="LeftTriggerRouteActivatorSelectedInput"/>.</summary>
        public InputChoice RightTriggerRouteActivatorSelectedInput
        {
            get
            {
                if (string.IsNullOrEmpty(_rightTriggerRouteActivator)) return null;
                foreach (var c in SlotAvailableInputs)
                {
                    if (c == null) continue;
                    if (string.Equals(c.Descriptor, _rightTriggerRouteActivator, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(c.DeviceGuid ?? "", _rightTriggerRouteActivatorDeviceGuid ?? "", StringComparison.OrdinalIgnoreCase))
                        return c;
                }
                return null;
            }
            set
            {
                if (value == null) return;
                RightTriggerRouteActivator = value.Descriptor ?? "";
                RightTriggerRouteActivatorDeviceGuid = value.DeviceGuid ?? "";
            }
        }
        public void OnRightTriggerRouteActivatorSelectedInputRefresh()
            => OnPropertyChanged(nameof(RightTriggerRouteActivatorSelectedInput));

        private bool _rightTriggerRouteActivatorRecording;
        public bool RightTriggerRouteActivatorRecording
        {
            get => _rightTriggerRouteActivatorRecording;
            set
            {
                if (SetProperty(ref _rightTriggerRouteActivatorRecording, value))
                {
                    OnPropertyChanged(nameof(RightTriggerRouteActivatorRecordButtonIcon));
                    OnPropertyChanged(nameof(RightTriggerRouteActivatorRecordButtonText));
                }
            }
        }
        public string RightTriggerRouteActivatorRecordButtonIcon => _rightTriggerRouteActivatorRecording ? "" : "";
        public string RightTriggerRouteActivatorRecordButtonText => _rightTriggerRouteActivatorRecording
            ? Strings.Instance.Common_Recording
            : Strings.Instance.Common_Record;
        public event EventHandler RightTriggerRouteActivatorRecordRequested;
        public void FireRightTriggerRouteActivatorRecord() => RightTriggerRouteActivatorRecordRequested?.Invoke(this, EventArgs.Empty);
        private RelayCommand _rightTriggerRouteActivatorRecordCommand;
        public RelayCommand RightTriggerRouteActivatorRecordCommand =>
            _rightTriggerRouteActivatorRecordCommand ??= new RelayCommand(FireRightTriggerRouteActivatorRecord);

        private RelayCommand _resetRightTriggerRouteActivatorCommand;
        public RelayCommand ResetRightTriggerRouteActivatorCommand =>
            _resetRightTriggerRouteActivatorCommand ??= new RelayCommand(() =>
            {
                RightTriggerRouteActivator = "";
                RightTriggerRouteActivatorDeviceGuid = "";
            });

        /// <summary>Resets the whole Trigger Routing card to defaults (#102).</summary>
        private RelayCommand _resetTriggerRouteCardCommand;
        public RelayCommand ResetTriggerRouteCardCommand =>
            _resetTriggerRouteCardCommand ??= new RelayCommand(() =>
            {
                LeftTriggerRouteSource = "None";
                LeftTriggerRouteMode = "Duplicate";
                LeftTriggerRouteScale = 100;
                LeftTriggerRouteActivator = "";
                LeftTriggerRouteActivatorDeviceGuid = "";
                LeftTriggerRouteActivatorMode = "Hold";
                RightTriggerRouteSource = "None";
                RightTriggerRouteMode = "Duplicate";
                RightTriggerRouteScale = 100;
                RightTriggerRouteActivator = "";
                RightTriggerRouteActivatorDeviceGuid = "";
                RightTriggerRouteActivatorMode = "Hold";
            });

        // Per-option resets for the Trigger Routing card (#102).
        private RelayCommand _resetLeftTriggerRouteSourceCommand;
        public RelayCommand ResetLeftTriggerRouteSourceCommand =>
            _resetLeftTriggerRouteSourceCommand ??= new RelayCommand(() => LeftTriggerRouteSource = "None");
        private RelayCommand _resetLeftTriggerRouteModeCommand;
        public RelayCommand ResetLeftTriggerRouteModeCommand =>
            _resetLeftTriggerRouteModeCommand ??= new RelayCommand(() => LeftTriggerRouteMode = "Duplicate");
        private RelayCommand _resetLeftTriggerRouteScaleCommand;
        public RelayCommand ResetLeftTriggerRouteScaleCommand =>
            _resetLeftTriggerRouteScaleCommand ??= new RelayCommand(() => LeftTriggerRouteScale = 100);
        private RelayCommand _resetLeftTriggerRouteActivatorModeCommand;
        public RelayCommand ResetLeftTriggerRouteActivatorModeCommand =>
            _resetLeftTriggerRouteActivatorModeCommand ??= new RelayCommand(() => LeftTriggerRouteActivatorMode = "Hold");

        private RelayCommand _resetRightTriggerRouteSourceCommand;
        public RelayCommand ResetRightTriggerRouteSourceCommand =>
            _resetRightTriggerRouteSourceCommand ??= new RelayCommand(() => RightTriggerRouteSource = "None");
        private RelayCommand _resetRightTriggerRouteModeCommand;
        public RelayCommand ResetRightTriggerRouteModeCommand =>
            _resetRightTriggerRouteModeCommand ??= new RelayCommand(() => RightTriggerRouteMode = "Duplicate");
        private RelayCommand _resetRightTriggerRouteScaleCommand;
        public RelayCommand ResetRightTriggerRouteScaleCommand =>
            _resetRightTriggerRouteScaleCommand ??= new RelayCommand(() => RightTriggerRouteScale = 100);
        private RelayCommand _resetRightTriggerRouteActivatorModeCommand;
        public RelayCommand ResetRightTriggerRouteActivatorModeCommand =>
            _resetRightTriggerRouteActivatorModeCommand ??= new RelayCommand(() => RightTriggerRouteActivatorMode = "Hold");

        private int _forceOverallGain = 100;
        public int ForceOverallGain { get => _forceOverallGain; set => SetProperty(ref _forceOverallGain, Math.Clamp(value, 0, 100)); }

        private int _leftMotorStrength = 100;
        public int LeftMotorStrength { get => _leftMotorStrength; set => SetProperty(ref _leftMotorStrength, Math.Clamp(value, 0, 100)); }

        private int _rightMotorStrength = 100;
        public int RightMotorStrength { get => _rightMotorStrength; set => SetProperty(ref _rightMotorStrength, Math.Clamp(value, 0, 100)); }

        private bool _swapMotors;
        public bool SwapMotors { get => _swapMotors; set => SetProperty(ref _swapMotors, value); }

        private int _wheelRotationRange = 900;
        /// <summary>Native-FFB wheel hardware rotation range (40–1080°). Persisted
        /// in PadSetting.RotationRange; applied via the vendor HID writer in Step 2.</summary>
        public int WheelRotationRange { get => _wheelRotationRange; set => SetProperty(ref _wheelRotationRange, Math.Clamp(value, 40, 2520)); }

        private int _wheelAutoCenter;
        /// <summary>Native-FFB wheel auto-center strength (0–100%; 0 = off).
        /// Persisted in PadSetting.AutoCenterStrength.</summary>
        public int WheelAutoCenter { get => _wheelAutoCenter; set => SetProperty(ref _wheelAutoCenter, Math.Clamp(value, 0, 100)); }

        private bool _wheelRpmLeds;
        /// <summary>Drive the wheel's RPM / shift LEDs from game telemetry (Logitech /
        /// Fanatec). Persisted in PadSetting.WheelRpmLeds; consumed by Step 2.</summary>
        public bool WheelRpmLeds { get => _wheelRpmLeds; set => SetProperty(ref _wheelRpmLeds, value); }

        private RelayCommand _resetWheelRotationRangeCommand;
        public RelayCommand ResetWheelRotationRangeCommand =>
            _resetWheelRotationRangeCommand ??= new RelayCommand(() => WheelRotationRange = 900);

        private RelayCommand _resetWheelAutoCenterCommand;
        public RelayCommand ResetWheelAutoCenterCommand =>
            _resetWheelAutoCenterCommand ??= new RelayCommand(() => WheelAutoCenter = 0);

        private RelayCommand _resetWheelRpmLedsCommand;
        public RelayCommand ResetWheelRpmLedsCommand =>
            _resetWheelRpmLedsCommand ??= new RelayCommand(() => WheelRpmLeds = false);

        private RelayCommand _resetWheelAllCommand;
        public RelayCommand ResetWheelAllCommand =>
            _resetWheelAllCommand ??= new RelayCommand(() =>
            {
                WheelRotationRange = 900;
                WheelAutoCenter = 0;
                WheelRpmLeds = false;
            });

        private ICommand _resetForceAllCommand;
        public ICommand ResetForceAllCommand => _resetForceAllCommand ??= new RelayCommand(() =>
        {
            ForceOverallGain = 100;
            LeftMotorStrength = 100;
            RightMotorStrength = 100;
            SwapMotors = false;
            AudioRumbleEnabled = false;
            AudioRumbleSensitivity = 4.0;
            AudioRumbleCutoffHz = 80.0;
            AudioRumbleLeftMotor = 100;
            AudioRumbleRightMotor = 100;
            ConstantForceEnabled = false;
            ConstantForceX = 0;
            ConstantForceY = 0;
        });

        // ── Impulse Triggers (Xbox One+ per-trigger motors) ──
        // Gated on at least one assigned device exposing
        // SDL_PROP_JOYSTICK_CAP_TRIGGER_RUMBLE_BOOLEAN. Settings round-trip
        // through PadSetting.ImpulseLeftStrength / ImpulseRightStrength /
        // ImpulseSwapTriggers; meter bars feed from FinalVibrationStates +
        // SelectedDeviceVibrationStates (trigger-motor fields populated by
        // ComputeFinalVibrationStates).

        private int _impulseOverallGain = 100;
        public int ImpulseOverallGain { get => _impulseOverallGain; set => SetProperty(ref _impulseOverallGain, Math.Clamp(value, 0, 100)); }

        private int _impulseLeftStrength = 100;
        public int ImpulseLeftStrength { get => _impulseLeftStrength; set => SetProperty(ref _impulseLeftStrength, Math.Clamp(value, 0, 100)); }

        private int _impulseRightStrength = 100;
        public int ImpulseRightStrength { get => _impulseRightStrength; set => SetProperty(ref _impulseRightStrength, Math.Clamp(value, 0, 100)); }

        private bool _impulseSwapTriggers;
        public bool ImpulseSwapTriggers { get => _impulseSwapTriggers; set => SetProperty(ref _impulseSwapTriggers, value); }

        // ── Constant Trigger Force (Xbox One+ trigger-motor analogue of
        //    Constant Force). Two independent 0..1 magnitudes that the
        //    Engine ConstantTriggerForceEvaluator applies when game/macro
        //    trigger rumble is silent and the enable flag is on.
        private bool _constantTriggerForceEnabled;
        public bool ConstantTriggerForceEnabled
        {
            get => _constantTriggerForceEnabled;
            set => SetProperty(ref _constantTriggerForceEnabled, value);
        }

        private double _constantTriggerForceLeft;
        public double ConstantTriggerForceLeft
        {
            get => _constantTriggerForceLeft;
            set => SetProperty(ref _constantTriggerForceLeft, Math.Clamp(value, 0.0, 1.0));
        }

        private double _constantTriggerForceRight;
        public double ConstantTriggerForceRight
        {
            get => _constantTriggerForceRight;
            set => SetProperty(ref _constantTriggerForceRight, Math.Clamp(value, 0.0, 1.0));
        }

        // ── Audio Trigger Rumble (Xbox One+ trigger-motor analogue of
        //    Audio Bass Rumble). Shares the slot's
        //    AudioBassDetector but uses its own parallel filter chain
        //    with independent Sensitivity / CutoffHz. Per-trigger
        //    scales also independent.
        private bool _audioRumbleTriggersEnabled;
        public bool AudioRumbleTriggersEnabled
        {
            get => _audioRumbleTriggersEnabled;
            set => SetProperty(ref _audioRumbleTriggersEnabled, value);
        }

        private double _audioRumbleTriggersSensitivity = 4.0;
        public double AudioRumbleTriggersSensitivity
        {
            get => _audioRumbleTriggersSensitivity;
            set => SetProperty(ref _audioRumbleTriggersSensitivity, Math.Clamp(value, 1.0, 20.0));
        }

        private double _audioRumbleTriggersCutoffHz = 80.0;
        public double AudioRumbleTriggersCutoffHz
        {
            get => _audioRumbleTriggersCutoffHz;
            set => SetProperty(ref _audioRumbleTriggersCutoffHz, Math.Clamp(value, 20.0, 200.0));
        }

        private int _audioRumbleLeftTrigger = 100;
        public int AudioRumbleLeftTrigger
        {
            get => _audioRumbleLeftTrigger;
            set => SetProperty(ref _audioRumbleLeftTrigger, Math.Clamp(value, 0, 100));
        }

        private int _audioRumbleRightTrigger = 100;
        public int AudioRumbleRightTrigger
        {
            get => _audioRumbleRightTrigger;
            set => SetProperty(ref _audioRumbleRightTrigger, Math.Clamp(value, 0, 100));
        }

        private double _audioRumbleTriggersLevelMeter;
        public double AudioRumbleTriggersLevelMeter
        {
            get => _audioRumbleTriggersLevelMeter;
            set => SetProperty(ref _audioRumbleTriggersLevelMeter, value);
        }

        private double _deviceLeftTriggerMotorDisplay;
        public double DeviceLeftTriggerMotorDisplay { get => _deviceLeftTriggerMotorDisplay; set => SetProperty(ref _deviceLeftTriggerMotorDisplay, value); }

        private double _deviceRightTriggerMotorDisplay;
        public double DeviceRightTriggerMotorDisplay { get => _deviceRightTriggerMotorDisplay; set => SetProperty(ref _deviceRightTriggerMotorDisplay, value); }

        private ICommand _resetImpulseAllCommand;
        public ICommand ResetImpulseAllCommand => _resetImpulseAllCommand ??= new RelayCommand(() =>
        {
            ImpulseOverallGain = 100;
            ImpulseLeftStrength = 100;
            ImpulseRightStrength = 100;
            ImpulseSwapTriggers = false;
            ConstantTriggerForceEnabled = false;
            ConstantTriggerForceLeft = 0;
            ConstantTriggerForceRight = 0;
            AudioRumbleTriggersEnabled = false;
            AudioRumbleTriggersSensitivity = 4.0;
            AudioRumbleTriggersCutoffHz = 80.0;
            AudioRumbleLeftTrigger = 100;
            AudioRumbleRightTrigger = 100;
        });

        private ICommand _resetImpulseOverallGainCommand;
        public ICommand ResetImpulseOverallGainCommand => _resetImpulseOverallGainCommand ??= new RelayCommand(() => ImpulseOverallGain = 100);
        private ICommand _resetImpulseLeftCommand;
        public ICommand ResetImpulseLeftCommand => _resetImpulseLeftCommand ??= new RelayCommand(() => ImpulseLeftStrength = 100);
        private ICommand _resetImpulseRightCommand;
        public ICommand ResetImpulseRightCommand => _resetImpulseRightCommand ??= new RelayCommand(() => ImpulseRightStrength = 100);

        // ── Constant Trigger Force reset commands ──
        private ICommand _resetConstantTriggerForceCommand;
        public ICommand ResetConstantTriggerForceCommand => _resetConstantTriggerForceCommand ??= new RelayCommand(() =>
        {
            ConstantTriggerForceEnabled = false;
            ConstantTriggerForceLeft = 0;
            ConstantTriggerForceRight = 0;
        });

        private ICommand _resetConstantTriggerLeftCommand;
        public ICommand ResetConstantTriggerLeftCommand => _resetConstantTriggerLeftCommand ??= new RelayCommand(() => ConstantTriggerForceLeft = 0);
        private ICommand _resetConstantTriggerRightCommand;
        public ICommand ResetConstantTriggerRightCommand => _resetConstantTriggerRightCommand ??= new RelayCommand(() => ConstantTriggerForceRight = 0);

        // ── Audio Trigger Rumble reset commands ──
        private ICommand _resetAudioTriggerRumbleAllCommand;
        public ICommand ResetAudioTriggerRumbleAllCommand => _resetAudioTriggerRumbleAllCommand ??= new RelayCommand(() =>
        {
            AudioRumbleTriggersEnabled = false;
            AudioRumbleTriggersSensitivity = 4.0;
            AudioRumbleTriggersCutoffHz = 80.0;
            AudioRumbleLeftTrigger = 100;
            AudioRumbleRightTrigger = 100;
        });

        private ICommand _resetAudioTriggerSensitivityCommand;
        public ICommand ResetAudioTriggerSensitivityCommand => _resetAudioTriggerSensitivityCommand ??= new RelayCommand(() => AudioRumbleTriggersSensitivity = 4.0);
        private ICommand _resetAudioTriggerCutoffCommand;
        public ICommand ResetAudioTriggerCutoffCommand => _resetAudioTriggerCutoffCommand ??= new RelayCommand(() => AudioRumbleTriggersCutoffHz = 80.0);
        private ICommand _resetAudioLeftTriggerCommand;
        public ICommand ResetAudioLeftTriggerCommand => _resetAudioLeftTriggerCommand ??= new RelayCommand(() => AudioRumbleLeftTrigger = 100);
        private ICommand _resetAudioRightTriggerCommand;
        public ICommand ResetAudioRightTriggerCommand => _resetAudioRightTriggerCommand ??= new RelayCommand(() => AudioRumbleRightTrigger = 100);

        private ICommand _resetOverallGainCommand;
        public ICommand ResetOverallGainCommand => _resetOverallGainCommand ??= new RelayCommand(() => ForceOverallGain = 100);
        private ICommand _resetLeftMotorCommand;
        public ICommand ResetLeftMotorCommand => _resetLeftMotorCommand ??= new RelayCommand(() => LeftMotorStrength = 100);
        private ICommand _resetRightMotorCommand;
        public ICommand ResetRightMotorCommand => _resetRightMotorCommand ??= new RelayCommand(() => RightMotorStrength = 100);

        private double _leftMotorDisplay;
        /// <summary>Slot-wide motor activity (max across every device
        /// mapped to the slot, each scaled by its own PadSetting). Drives
        /// the Controller-preview-tab motor bar — that meter is
        /// device-filter-independent so a force coming through any device
        /// shows up regardless of the FFB tab's dropdown selection.</summary>
        public double LeftMotorDisplay { get => _leftMotorDisplay; set => SetProperty(ref _leftMotorDisplay, value); }

        private double _rightMotorDisplay;
        public double RightMotorDisplay { get => _rightMotorDisplay; set => SetProperty(ref _rightMotorDisplay, value); }

        private double _deviceLeftMotorDisplay;
        /// <summary>Selected device's own motor activity (its PadSetting's
        /// gain / motor strengths / audio rumble / constant force applied
        /// to the slot's raw vibration). Drives the FFB-tab motor bar —
        /// that meter MUST be device-specific so users editing one device's
        /// FFB settings see what's effectively reaching THAT device, not
        /// the slot-wide max.</summary>
        public double DeviceLeftMotorDisplay { get => _deviceLeftMotorDisplay; set => SetProperty(ref _deviceLeftMotorDisplay, value); }

        private double _deviceRightMotorDisplay;
        public double DeviceRightMotorDisplay { get => _deviceRightMotorDisplay; set => SetProperty(ref _deviceRightMotorDisplay, value); }

        // ── Audio Bass Rumble (per-device) ──

        private bool _audioRumbleEnabled;
        public bool AudioRumbleEnabled { get => _audioRumbleEnabled; set => SetProperty(ref _audioRumbleEnabled, value); }

        private double _audioRumbleSensitivity = 4.0;
        public double AudioRumbleSensitivity { get => _audioRumbleSensitivity; set => SetProperty(ref _audioRumbleSensitivity, Math.Clamp(value, 1, 20)); }

        private double _audioRumbleCutoffHz = 80.0;
        public double AudioRumbleCutoffHz { get => _audioRumbleCutoffHz; set => SetProperty(ref _audioRumbleCutoffHz, Math.Clamp(value, 20, 200)); }

        private int _audioRumbleLeftMotor = 100;
        public int AudioRumbleLeftMotor { get => _audioRumbleLeftMotor; set => SetProperty(ref _audioRumbleLeftMotor, Math.Clamp(value, 0, 100)); }

        private int _audioRumbleRightMotor = 100;
        public int AudioRumbleRightMotor { get => _audioRumbleRightMotor; set => SetProperty(ref _audioRumbleRightMotor, Math.Clamp(value, 0, 100)); }

        private double _audioRumbleLevelMeter;
        public double AudioRumbleLevelMeter { get => _audioRumbleLevelMeter; set => SetProperty(ref _audioRumbleLevelMeter, value); }

        private ICommand _resetAudioRumbleAllCommand;
        public ICommand ResetAudioRumbleAllCommand => _resetAudioRumbleAllCommand ??= new RelayCommand(() =>
        {
            AudioRumbleEnabled = false;
            AudioRumbleSensitivity = 4.0;
            AudioRumbleCutoffHz = 80.0;
            AudioRumbleLeftMotor = 100;
            AudioRumbleRightMotor = 100;
        });

        private ICommand _resetAudioSensitivityCommand;
        public ICommand ResetAudioSensitivityCommand => _resetAudioSensitivityCommand ??= new RelayCommand(() => AudioRumbleSensitivity = 4.0);
        private ICommand _resetAudioCutoffCommand;
        public ICommand ResetAudioCutoffCommand => _resetAudioCutoffCommand ??= new RelayCommand(() => AudioRumbleCutoffHz = 80.0);
        private ICommand _resetAudioLeftMotorCommand;
        public ICommand ResetAudioLeftMotorCommand => _resetAudioLeftMotorCommand ??= new RelayCommand(() => AudioRumbleLeftMotor = 100);
        private ICommand _resetAudioRightMotorCommand;
        public ICommand ResetAudioRightMotorCommand => _resetAudioRightMotorCommand ??= new RelayCommand(() => AudioRumbleRightMotor = 100);

        // ── Constant Force (per-device) ──
        // PadForge-driven continuous force on the physical device. When
        // enabled, applies until toggled off OR until a game/program
        // emits its own non-zero force; resumes when the game returns
        // to zero. X and Y are normalized [-1, +1]; Y+ is up in the UI
        // grid (engine converts to HID polar internally).

        private bool _constantForceEnabled;
        public bool ConstantForceEnabled { get => _constantForceEnabled; set => SetProperty(ref _constantForceEnabled, value); }

        // ── Steering at-lock feedback (#94), per slot ──
        private bool _steeringLockRumbleEnabled;
        public bool SteeringLockRumbleEnabled { get => _steeringLockRumbleEnabled; set => SetProperty(ref _steeringLockRumbleEnabled, value); }
        private bool _steeringLockTriggerVibEnabled;
        public bool SteeringLockTriggerVibEnabled { get => _steeringLockTriggerVibEnabled; set => SetProperty(ref _steeringLockTriggerVibEnabled, value); }
        private bool _steeringLockLightbarEnabled;
        public bool SteeringLockLightbarEnabled { get => _steeringLockLightbarEnabled; set => SetProperty(ref _steeringLockLightbarEnabled, value); }
        private bool _steeringLockAtResistanceEnabled;
        public bool SteeringLockATResistanceEnabled { get => _steeringLockAtResistanceEnabled; set => SetProperty(ref _steeringLockAtResistanceEnabled, value); }
        private double _steeringLockPulseMs = 80;
        public double SteeringLockPulseMs { get => _steeringLockPulseMs; set => SetProperty(ref _steeringLockPulseMs, Math.Clamp(value, 0, 2000)); }
        private string _steeringLockLightbarColor = "#FF0000";
        public string SteeringLockLightbarColor
        {
            get => _steeringLockLightbarColor;
            set
            {
                if (SetProperty(ref _steeringLockLightbarColor, NormalizeSteeringLockColor(value)))
                {
                    OnPropertyChanged(nameof(SteeringLockColorR));
                    OnPropertyChanged(nameof(SteeringLockColorG));
                    OnPropertyChanged(nameof(SteeringLockColorB));
                }
            }
        }

        // Canonicalize hex-field input (accepts with/without '#', any case) to "#RRGGBB".
        // Anything unparseable falls back to red, so the picker, sliders, swatch, and hex
        // field stay in sync no matter what the user types.
        private static string NormalizeSteeringLockColor(string value)
        {
            string s = (value ?? "").Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length == 6
                && byte.TryParse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte r)
                && byte.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte g)
                && byte.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte b))
                return FormatSteeringLockColor(r, g, b);
            return "#FF0000";
        }

        // Lightbar pulse colour exposed as R/G/B bytes for the shared ColorPickerControl,
        // backed by the persisted hex string above (the engine reads the hex form). Keeps
        // this colour setting consistent with every other lightbar colour in the app
        // instead of a raw hex text box. Writes funnel back through the hex setter, so the
        // existing dirty/save wiring on SteeringLockLightbarColor still fires.
        public byte SteeringLockColorR { get => ParseSteeringLockColor().r; set { var c = ParseSteeringLockColor(); SteeringLockLightbarColor = FormatSteeringLockColor(value, c.g, c.b); } }
        public byte SteeringLockColorG { get => ParseSteeringLockColor().g; set { var c = ParseSteeringLockColor(); SteeringLockLightbarColor = FormatSteeringLockColor(c.r, value, c.b); } }
        public byte SteeringLockColorB { get => ParseSteeringLockColor().b; set { var c = ParseSteeringLockColor(); SteeringLockLightbarColor = FormatSteeringLockColor(c.r, c.g, value); } }

        private (byte r, byte g, byte b) ParseSteeringLockColor()
        {
            string s = (_steeringLockLightbarColor ?? "#FF0000").Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length == 6
                && byte.TryParse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte r)
                && byte.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte g)
                && byte.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte b))
                return (r, g, b);
            return (0xFF, 0x00, 0x00);
        }

        private static string FormatSteeringLockColor(byte r, byte g, byte b)
            => string.Format(System.Globalization.CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", r, g, b);

        private double _steeringLockLightbarHoldMs = 80;
        public double SteeringLockLightbarHoldMs { get => _steeringLockLightbarHoldMs; set => SetProperty(ref _steeringLockLightbarHoldMs, Math.Clamp(value, 0, 2000)); }

        private double _steeringLockLightbarFadeMs = 250;
        public double SteeringLockLightbarFadeMs { get => _steeringLockLightbarFadeMs; set => SetProperty(ref _steeringLockLightbarFadeMs, Math.Clamp(value, 0, 5000)); }

        // ── Steering-lock lightbar color source + dedicated palette (#94) ──
        // Mirrors the macro lightbar's three modes (Fixed base color / RandomHue / PaletteStep).
        // The palette is dedicated to the steering lock — never shared with the Lighting tab or
        // a macro. Per device, so it swaps with SelectedMappedDevice: the CSV setter rebuilds the
        // bound collection on every swap.
        private MacroLightbarColorSource _steeringLockLightbarColorSource = MacroLightbarColorSource.Fixed;
        public MacroLightbarColorSource SteeringLockLightbarColorSource
        {
            get => _steeringLockLightbarColorSource;
            set
            {
                if (SetProperty(ref _steeringLockLightbarColorSource, value))
                {
                    OnPropertyChanged(nameof(IsSteeringLockLightbarFixedColorVisible));
                    OnPropertyChanged(nameof(IsSteeringLockLightbarPaletteVisible));
                }
            }
        }

        /// <summary>Show the fixed-color picker only for the Fixed source.</summary>
        public bool IsSteeringLockLightbarFixedColorVisible => _steeringLockLightbarColorSource == MacroLightbarColorSource.Fixed;
        /// <summary>Show the dedicated palette editor only for the PaletteStep source.</summary>
        public bool IsSteeringLockLightbarPaletteVisible => _steeringLockLightbarColorSource == MacroLightbarColorSource.PaletteStep;

        private string _steeringLockLightbarPaletteCsv = string.Empty;
        public string SteeringLockLightbarPaletteCsv
        {
            get => _steeringLockLightbarPaletteCsv;
            set
            {
                string v = value ?? string.Empty;
                if (_steeringLockLightbarPaletteCsv == v) return;
                _steeringLockLightbarPaletteCsv = v;
                RebuildSteeringLockPaletteFromCsv();   // keep the bound collection in step (device swap)
                OnPropertyChanged();
            }
        }

        private System.Collections.ObjectModel.ObservableCollection<LightbarPaletteEntry> _steeringLockLightbarPalette;
        private bool _syncingSteeringLockPalette;

        /// <summary>Editable view of the steering-lock palette, bound by the card's ItemsControl.
        /// Edits write back to <see cref="SteeringLockLightbarPaletteCsv"/>.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public System.Collections.ObjectModel.ObservableCollection<LightbarPaletteEntry> SteeringLockLightbarPalette
        {
            get
            {
                if (_steeringLockLightbarPalette == null)
                {
                    _steeringLockLightbarPalette = new System.Collections.ObjectModel.ObservableCollection<LightbarPaletteEntry>();
                    _steeringLockLightbarPalette.CollectionChanged += (_, e) =>
                    {
                        if (e.NewItems != null)
                            foreach (LightbarPaletteEntry n in e.NewItems) n.PropertyChanged += OnSteeringLockPaletteEntryChanged;
                        if (e.OldItems != null)
                            foreach (LightbarPaletteEntry o in e.OldItems) o.PropertyChanged -= OnSteeringLockPaletteEntryChanged;
                        SyncSteeringLockPaletteCsv();   // no-op while the guard is set (populate)
                    };
                    PopulateSteeringLockPalette(_steeringLockLightbarPaletteCsv);
                }
                return _steeringLockLightbarPalette;
            }
        }

        // (Re)fills the bound collection from a CSV. Manually unsubscribes before the Clear
        // (a Reset raises no OldItems), then lets CollectionChanged re-subscribe each Add.
        // Guarded so neither the clear nor the adds write back over the CSV being loaded.
        private void PopulateSteeringLockPalette(string csv)
        {
            _syncingSteeringLockPalette = true;
            try
            {
                foreach (var e in _steeringLockLightbarPalette) e.PropertyChanged -= OnSteeringLockPaletteEntryChanged;
                _steeringLockLightbarPalette.Clear();
                foreach (var (r, g, b) in ParseLightbarPaletteCsv(csv))
                    _steeringLockLightbarPalette.Add(new LightbarPaletteEntry { R = r, G = g, B = b });
            }
            finally { _syncingSteeringLockPalette = false; }
        }

        private void RebuildSteeringLockPaletteFromCsv()
        {
            if (_steeringLockLightbarPalette == null) return; // not materialized; getter builds it
            PopulateSteeringLockPalette(_steeringLockLightbarPaletteCsv);
            OnPropertyChanged(nameof(SteeringLockLightbarPalette));
        }

        private void OnSteeringLockPaletteEntryChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(LightbarPaletteEntry.R) or nameof(LightbarPaletteEntry.G) or nameof(LightbarPaletteEntry.B))
                SyncSteeringLockPaletteCsv();
        }

        private void SyncSteeringLockPaletteCsv()
        {
            if (_syncingSteeringLockPalette || _steeringLockLightbarPalette == null) return;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _steeringLockLightbarPalette.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = _steeringLockLightbarPalette[i];
                sb.Append($"{e.R:X2}{e.G:X2}{e.B:X2}");
            }
            string csv = sb.ToString();
            if (_steeringLockLightbarPaletteCsv != csv)
            {
                _steeringLockLightbarPaletteCsv = csv;   // direct write; the collection is already current
                OnPropertyChanged(nameof(SteeringLockLightbarPaletteCsv));
            }
        }

        private static System.Collections.Generic.IEnumerable<(byte r, byte g, byte b)> ParseLightbarPaletteCsv(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) yield break;
            foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (raw.Length != 6) continue;
                if (byte.TryParse(raw.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
                 && byte.TryParse(raw.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
                 && byte.TryParse(raw.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                    yield return (r, g, b);
            }
        }

        private ICommand _addSteeringLockPaletteColorCommand;
        public ICommand AddSteeringLockPaletteColorCommand => _addSteeringLockPaletteColorCommand ??= new RelayCommand(() =>
            SteeringLockLightbarPalette.Add(new LightbarPaletteEntry { R = 0xFF, G = 0xFF, B = 0xFF }));

        private ICommand _removeSteeringLockPaletteColorCommand;
        public ICommand RemoveSteeringLockPaletteColorCommand => _removeSteeringLockPaletteColorCommand ??= new RelayCommand<LightbarPaletteEntry>(entry =>
        { if (entry != null) SteeringLockLightbarPalette.Remove(entry); });

        // Per-channel colour resets (match the other lightbar pickers); each resets its
        // channel to the #FF0000 default component, funneling through the hex setter.
        private ICommand _resetSteeringLockColorRCommand;
        public ICommand ResetSteeringLockColorRCommand => _resetSteeringLockColorRCommand ??= new RelayCommand(() => SteeringLockColorR = 0xFF);
        private ICommand _resetSteeringLockColorGCommand;
        public ICommand ResetSteeringLockColorGCommand => _resetSteeringLockColorGCommand ??= new RelayCommand(() => SteeringLockColorG = 0x00);
        private ICommand _resetSteeringLockColorBCommand;
        public ICommand ResetSteeringLockColorBCommand => _resetSteeringLockColorBCommand ??= new RelayCommand(() => SteeringLockColorB = 0x00);
        private ICommand _resetSteeringLockPulseCommand;
        public ICommand ResetSteeringLockPulseCommand => _resetSteeringLockPulseCommand ??= new RelayCommand(() => SteeringLockPulseMs = 80);
        private ICommand _resetSteeringLockLightbarHoldCommand;
        public ICommand ResetSteeringLockLightbarHoldCommand => _resetSteeringLockLightbarHoldCommand ??= new RelayCommand(() => SteeringLockLightbarHoldMs = 80);
        private ICommand _resetSteeringLockFadeCommand;
        public ICommand ResetSteeringLockFadeCommand => _resetSteeringLockFadeCommand ??= new RelayCommand(() => SteeringLockLightbarFadeMs = 250);

        // Per-channel resets (each channel defaults to off), matching the per-row reset
        // buttons on the sliders so every setting in the card has its own reset.
        private ICommand _resetSteeringLockRumbleCommand;
        public ICommand ResetSteeringLockRumbleCommand => _resetSteeringLockRumbleCommand ??= new RelayCommand(() => SteeringLockRumbleEnabled = false);
        private ICommand _resetSteeringLockTriggerVibCommand;
        public ICommand ResetSteeringLockTriggerVibCommand => _resetSteeringLockTriggerVibCommand ??= new RelayCommand(() => SteeringLockTriggerVibEnabled = false);
        private ICommand _resetSteeringLockLightbarCommand;
        public ICommand ResetSteeringLockLightbarCommand => _resetSteeringLockLightbarCommand ??= new RelayCommand(() => SteeringLockLightbarEnabled = false);
        private ICommand _resetSteeringLockResistanceCommand;
        public ICommand ResetSteeringLockResistanceCommand => _resetSteeringLockResistanceCommand ??= new RelayCommand(() => SteeringLockATResistanceEnabled = false);

        // Reset all steering-lock-feedback settings to defaults (every channel off, pulse
        // 80ms, fade 250ms, colour #FF0000). Each setter fires PropertyChanged, which the
        // MainWindow handler turns into MarkDirty, so the reset persists like a manual edit.
        private ICommand _resetSteeringLockAllCommand;
        public ICommand ResetSteeringLockAllCommand => _resetSteeringLockAllCommand ??= new RelayCommand(() =>
        {
            SteeringLockRumbleEnabled = false;
            SteeringLockTriggerVibEnabled = false;
            SteeringLockLightbarEnabled = false;
            SteeringLockATResistanceEnabled = false;
            SteeringLockPulseMs = 80;
            SteeringLockLightbarHoldMs = 80;
            SteeringLockLightbarFadeMs = 250;
            SteeringLockLightbarColor = "#FF0000";
            SteeringLockLightbarColorSource = MacroLightbarColorSource.Fixed;
            SteeringLockLightbarPaletteCsv = string.Empty;
        });

        private double _constantForceX;
        public double ConstantForceX { get => _constantForceX; set => SetProperty(ref _constantForceX, Math.Clamp(value, -1.0, 1.0)); }

        private double _constantForceY;
        public double ConstantForceY { get => _constantForceY; set => SetProperty(ref _constantForceY, Math.Clamp(value, -1.0, 1.0)); }

        private ICommand _resetConstantForceCommand;
        public ICommand ResetConstantForceCommand => _resetConstantForceCommand ??= new RelayCommand(() =>
        {
            ConstantForceEnabled = false;
            ConstantForceX = 0;
            ConstantForceY = 0;
        });

        private ICommand _resetConstantForceXCommand;
        public ICommand ResetConstantForceXCommand => _resetConstantForceXCommand ??= new RelayCommand(() => ConstantForceX = 0);

        private ICommand _resetConstantForceYCommand;
        public ICommand ResetConstantForceYCommand => _resetConstantForceYCommand ??= new RelayCommand(() => ConstantForceY = 0);

        // ═══════════════════════════════════════════════
        //  #2: Expanded deadzone settings
        //  Per-axis X/Y, anti-deadzone, linear, trigger deadzones
        // ═══════════════════════════════════════════════

        /// <summary>
        /// Resets all per-slot settings to defaults. Called when a slot is deleted
        /// so the next controller created in the same slot starts clean.
        /// </summary>
        public void ResetAllSettings()
        {
            ResetDeadZoneSettings();
            LeftSensitivityCurveX = "0,0;1,1";
            LeftSensitivityCurveY = "0,0;1,1";
            RightSensitivityCurveX = "0,0;1,1";
            RightSensitivityCurveY = "0,0;1,1";
            LeftTriggerSensitivityCurve = "0,0;1,1";
            RightTriggerSensitivityCurve = "0,0;1,1";
            ForceOverallGain = 100;
            LeftMotorStrength = 100;
            RightMotorStrength = 100;
            ImpulseOverallGain = 100;
            ImpulseLeftStrength = 100;
            ImpulseRightStrength = 100;
            ImpulseSwapTriggers = false;
            ConstantTriggerForceEnabled = false;
            ConstantTriggerForceLeft = 0;
            ConstantTriggerForceRight = 0;
            AudioRumbleTriggersEnabled = false;
            AudioRumbleTriggersSensitivity = 4.0;
            AudioRumbleTriggersCutoffHz = 80.0;
            AudioRumbleLeftTrigger = 100;
            AudioRumbleRightTrigger = 100;
            ConstantForceEnabled = false;
            ConstantForceX = 0;
            ConstantForceY = 0;
            // Macros are bound to the slot, not the physical device, so a
            // slot deletion has to drop them. Otherwise the next VC created
            // at this pad index inherits the deleted slot's macros. Their
            // sounds go with them. A looping sound would be unstoppable.
            PadForge.Common.Input.SoundMacroService.StopSlot(PadIndex);
            Macros.Clear();

            // Menus (#9 B-17) are slot-scoped like macros: a slot deletion
            // or Reset to Defaults must drop them, or the next VC at this
            // index inherits the deleted slot's on-screen menus. The
            // definitions live on the slot's MappingSet, so clear the live
            // list too (Reset to Defaults already replaced the whole set;
            // DeleteSlot reaches only through here).
            {
                var menuSets = PadForge.Common.Input.SettingsManager.SlotMappingSets;
                if (menuSets != null && PadIndex >= 0 && PadIndex < menuSets.Length)
                {
                    menuSets[PadIndex]?.Menus?.Clear();
                    // Rumble-audio config (#236) is slot-scoped the same
                    // way: clear it AND publish synchronous silence, so
                    // the shaker tone dies with the slot instead of
                    // holding its last value until the poll lane's next
                    // tick (or forever, if the engine is stopped).
                    var delSet = menuSets[PadIndex];
                    if (delSet != null)
                    {
                        delSet.RumbleAudio = null;
                        // SOCD config (#240) is slot-scoped the same way:
                        // the next VC at this index must not inherit the
                        // deleted slot's pair cleaning.
                        delSet.SocdMode = "";
                        delSet.SocdPairs = "";
                    }
                }
            }
            PadForge.Common.Input.RumbleAudioService.SilenceSlot(PadIndex);
            ReloadMenus();
            ReloadRumbleAudio();
            ReloadSocd();

            // Per-device Lighting tab configs live in this PadViewModel's
            // dictionary, keyed by physical device InstanceGuid — not on
            // UserSetting, so DeleteSlot's UserSetting removal does not
            // reach them. Without this clear, deleting a slot and later
            // remapping the same physical device to a new slot would
            // resurrect that device's prior lightbar mode / colors /
            // palette via GetOrAdd in EnsureDeviceSlotConfigsForMappedDevices,
            // and the saved XML would still carry the stale entries.
            _perDeviceSlotConfigs.Clear();
            DeviceConfig = new DeviceSlotConfig();
        }

        /// <summary>Audit 2026-07-18 (transition-to-empty): clears the
        /// per-device config dictionary for a slot that is NOT created
        /// after a profile apply. Compaction shifts data down and the
        /// former tail index kept its dictionary, so creating a new slot
        /// there later and mapping the same device resurrected the old
        /// lighting / adaptive / synthetic-pressure config via GetOrAdd,
        /// the exact resurrection the DeleteSlot clear above prevents
        /// for the deleted index. DeviceConfig replacement is anchor-safe
        /// through the forwarder pattern.</summary>
        public void ClearPerDeviceConfigsForUncreatedSlot()
        {
            if (_perDeviceSlotConfigs.Count == 0) return;
            _perDeviceSlotConfigs.Clear();
            DeviceConfig = new DeviceSlotConfig();
        }

        /// <summary>Resets all deadzone, anti-deadzone, linear, and trigger settings to defaults.</summary>
        private void ResetDeadZoneSettings()
        {
            LeftDeadZoneShape = (int)DeadZoneShape.ScaledRadial;
            LeftDeadZoneX = 0; LeftDeadZoneY = 0;
            LeftAntiDeadZoneX = 0; LeftAntiDeadZoneY = 0;
            LeftLinear = 0;
            LeftCenterOffsetX = 0; LeftCenterOffsetY = 0;
            LeftMaxRangeX = 100; LeftMaxRangeY = 100;
            RightDeadZoneShape = (int)DeadZoneShape.ScaledRadial;
            RightDeadZoneX = 0; RightDeadZoneY = 0;
            RightAntiDeadZoneX = 0; RightAntiDeadZoneY = 0;
            RightLinear = 0;
            RightCenterOffsetX = 0; RightCenterOffsetY = 0;
            RightMaxRangeX = 100; RightMaxRangeY = 100;
            LeftTriggerDeadZone = 0; LeftTriggerAntiDeadZone = 0; LeftTriggerMaxRange = 100;
            RightTriggerDeadZone = 0; RightTriggerAntiDeadZone = 0; RightTriggerMaxRange = 100;
        }

        // ── Left Stick ──
        private int _leftDeadZoneShape = (int)DeadZoneShape.ScaledRadial;
        private static readonly int MaxDeadZoneShape = Enum.GetValues(typeof(DeadZoneShape)).Length - 1;
        public int LeftDeadZoneShape { get => _leftDeadZoneShape; set => SetProperty(ref _leftDeadZoneShape, Math.Clamp(value, 0, MaxDeadZoneShape)); }

        private double _leftDeadZoneX;
        public double LeftDeadZoneX { get => _leftDeadZoneX; set => SetProperty(ref _leftDeadZoneX, Math.Clamp(value, 0, 100)); }

        private double _leftDeadZoneY;
        public double LeftDeadZoneY { get => _leftDeadZoneY; set => SetProperty(ref _leftDeadZoneY, Math.Clamp(value, 0, 100)); }

        private double _leftAntiDeadZoneX;
        public double LeftAntiDeadZoneX { get => _leftAntiDeadZoneX; set => SetProperty(ref _leftAntiDeadZoneX, Math.Clamp(value, 0, 100)); }

        private double _leftAntiDeadZoneY;
        public double LeftAntiDeadZoneY { get => _leftAntiDeadZoneY; set => SetProperty(ref _leftAntiDeadZoneY, Math.Clamp(value, 0, 100)); }

        private double _leftLinear;
        public double LeftLinear { get => _leftLinear; set => SetProperty(ref _leftLinear, Math.Clamp(value, 0, 100)); }

        // ── Right Stick ──
        private int _rightDeadZoneShape = (int)DeadZoneShape.ScaledRadial;
        public int RightDeadZoneShape { get => _rightDeadZoneShape; set => SetProperty(ref _rightDeadZoneShape, Math.Clamp(value, 0, MaxDeadZoneShape)); }

        private double _rightDeadZoneX;
        public double RightDeadZoneX { get => _rightDeadZoneX; set => SetProperty(ref _rightDeadZoneX, Math.Clamp(value, 0, 100)); }

        private double _rightDeadZoneY;
        public double RightDeadZoneY { get => _rightDeadZoneY; set => SetProperty(ref _rightDeadZoneY, Math.Clamp(value, 0, 100)); }

        private double _rightAntiDeadZoneX;
        public double RightAntiDeadZoneX { get => _rightAntiDeadZoneX; set => SetProperty(ref _rightAntiDeadZoneX, Math.Clamp(value, 0, 100)); }

        private double _rightAntiDeadZoneY;
        public double RightAntiDeadZoneY { get => _rightAntiDeadZoneY; set => SetProperty(ref _rightAntiDeadZoneY, Math.Clamp(value, 0, 100)); }

        private double _rightLinear;
        public double RightLinear { get => _rightLinear; set => SetProperty(ref _rightLinear, Math.Clamp(value, 0, 100)); }

        // ── Sensitivity Curves (per-axis for sticks, serialized control point strings) ──
        private string _leftSensitivityCurveX = "0,0;1,1";
        public string LeftSensitivityCurveX { get => _leftSensitivityCurveX; set => SetProperty(ref _leftSensitivityCurveX, value ?? "0,0;1,1"); }
        private string _leftSensitivityCurveY = "0,0;1,1";
        public string LeftSensitivityCurveY { get => _leftSensitivityCurveY; set => SetProperty(ref _leftSensitivityCurveY, value ?? "0,0;1,1"); }

        private string _rightSensitivityCurveX = "0,0;1,1";
        public string RightSensitivityCurveX { get => _rightSensitivityCurveX; set => SetProperty(ref _rightSensitivityCurveX, value ?? "0,0;1,1"); }
        private string _rightSensitivityCurveY = "0,0;1,1";
        public string RightSensitivityCurveY { get => _rightSensitivityCurveY; set => SetProperty(ref _rightSensitivityCurveY, value ?? "0,0;1,1"); }

        private string _leftTriggerSensitivityCurve = "0,0;1,1";
        public string LeftTriggerSensitivityCurve { get => _leftTriggerSensitivityCurve; set => SetProperty(ref _leftTriggerSensitivityCurve, value ?? "0,0;1,1"); }

        private string _rightTriggerSensitivityCurve = "0,0;1,1";
        public string RightTriggerSensitivityCurve { get => _rightTriggerSensitivityCurve; set => SetProperty(ref _rightTriggerSensitivityCurve, value ?? "0,0;1,1"); }

        // ── Max Range ──
        private double _leftMaxRangeX = 100;
        public double LeftMaxRangeX { get => _leftMaxRangeX; set => SetProperty(ref _leftMaxRangeX, Math.Clamp(value, 1, 100)); }

        private double _leftMaxRangeY = 100;
        public double LeftMaxRangeY { get => _leftMaxRangeY; set => SetProperty(ref _leftMaxRangeY, Math.Clamp(value, 1, 100)); }

        private double _rightMaxRangeX = 100;
        public double RightMaxRangeX { get => _rightMaxRangeX; set => SetProperty(ref _rightMaxRangeX, Math.Clamp(value, 1, 100)); }

        private double _rightMaxRangeY = 100;
        public double RightMaxRangeY { get => _rightMaxRangeY; set => SetProperty(ref _rightMaxRangeY, Math.Clamp(value, 1, 100)); }

        // ── Max Range (negative direction) ──
        private double _leftMaxRangeXNeg = 100;
        public double LeftMaxRangeXNeg { get => _leftMaxRangeXNeg; set => SetProperty(ref _leftMaxRangeXNeg, Math.Clamp(value, 1, 100)); }

        private double _leftMaxRangeYNeg = 100;
        public double LeftMaxRangeYNeg { get => _leftMaxRangeYNeg; set => SetProperty(ref _leftMaxRangeYNeg, Math.Clamp(value, 1, 100)); }

        private double _rightMaxRangeXNeg = 100;
        public double RightMaxRangeXNeg { get => _rightMaxRangeXNeg; set => SetProperty(ref _rightMaxRangeXNeg, Math.Clamp(value, 1, 100)); }

        private double _rightMaxRangeYNeg = 100;
        public double RightMaxRangeYNeg { get => _rightMaxRangeYNeg; set => SetProperty(ref _rightMaxRangeYNeg, Math.Clamp(value, 1, 100)); }

        // ── Center Offsets ──
        private double _leftCenterOffsetX;
        public double LeftCenterOffsetX { get => _leftCenterOffsetX; set => SetProperty(ref _leftCenterOffsetX, Math.Clamp(value, -100, 100)); }

        private double _leftCenterOffsetY;
        public double LeftCenterOffsetY { get => _leftCenterOffsetY; set => SetProperty(ref _leftCenterOffsetY, Math.Clamp(value, -100, 100)); }

        private double _rightCenterOffsetX;
        public double RightCenterOffsetX { get => _rightCenterOffsetX; set => SetProperty(ref _rightCenterOffsetX, Math.Clamp(value, -100, 100)); }

        private double _rightCenterOffsetY;
        public double RightCenterOffsetY { get => _rightCenterOffsetY; set => SetProperty(ref _rightCenterOffsetY, Math.Clamp(value, -100, 100)); }

        // #174 boundary calibration: serialized per-angle maps, mirrored to/from
        // the StickConfigItem (which owns capture + the radar overlay) and
        // persisted to PadSetting.Left/RightThumbBoundaryMap. Empty = off.
        private string _leftThumbBoundaryMap = "";
        public string LeftThumbBoundaryMap { get => _leftThumbBoundaryMap; set => SetProperty(ref _leftThumbBoundaryMap, value ?? ""); }

        private string _rightThumbBoundaryMap = "";
        public string RightThumbBoundaryMap { get => _rightThumbBoundaryMap; set => SetProperty(ref _rightThumbBoundaryMap, value ?? ""); }

        // ── Triggers ──
        private double _leftTriggerDeadZone;
        public double LeftTriggerDeadZone { get => _leftTriggerDeadZone; set => SetProperty(ref _leftTriggerDeadZone, Math.Clamp(value, 0, 100)); }

        private double _rightTriggerDeadZone;
        public double RightTriggerDeadZone { get => _rightTriggerDeadZone; set => SetProperty(ref _rightTriggerDeadZone, Math.Clamp(value, 0, 100)); }

        private double _leftTriggerAntiDeadZone;
        public double LeftTriggerAntiDeadZone { get => _leftTriggerAntiDeadZone; set => SetProperty(ref _leftTriggerAntiDeadZone, Math.Clamp(value, 0, 100)); }

        private double _rightTriggerAntiDeadZone;
        public double RightTriggerAntiDeadZone { get => _rightTriggerAntiDeadZone; set => SetProperty(ref _rightTriggerAntiDeadZone, Math.Clamp(value, 0, 100)); }

        private double _leftTriggerMaxRange = 100;
        public double LeftTriggerMaxRange { get => _leftTriggerMaxRange; set => SetProperty(ref _leftTriggerMaxRange, Math.Clamp(value, 1, 100)); }

        private double _rightTriggerMaxRange = 100;
        public double RightTriggerMaxRange { get => _rightTriggerMaxRange; set => SetProperty(ref _rightTriggerMaxRange, Math.Clamp(value, 1, 100)); }

        // ── Backward compatibility shims ──
        // SettingsService and existing PadPage.xaml use LeftDeadZone/RightDeadZone.
        // Route to both X and Y axes so old code works transparently.
        public double LeftDeadZone
        {
            get => _leftDeadZoneX;
            set { LeftDeadZoneX = value; LeftDeadZoneY = value; }
        }

        public double RightDeadZone
        {
            get => _rightDeadZoneX;
            set { RightDeadZoneX = value; RightDeadZoneY = value; }
        }

        // ═══════════════════════════════════════════════
        //  Dynamic stick/trigger config items for the Sticks and Triggers tabs.
        //  These collections drive the ItemsControl-based dynamic UI.
        //  For gamepad presets: 2 sticks, 2 triggers.
        //  For custom Extended: N sticks, M triggers.
        // ═══════════════════════════════════════════════

        public ObservableCollection<StickConfigItem> StickConfigs { get; } = new();
        public ObservableCollection<TriggerConfigItem> TriggerConfigs { get; } = new();

        private bool _syncingConfigItems;

        /// <summary>
        /// Rebuilds the StickConfigs collection based on the current output type.
        /// For Xbox / PlayStation (or Extended with gamepad preset): always 2 sticks (Left, Right).
        /// For Extended Custom: N sticks based on ThumbstickCount.
        /// </summary>
        public void RebuildStickConfigs()
        {
            foreach (var item in StickConfigs)
                item.PropertyChanged -= OnStickConfigPropertyChanged;
            StickConfigs.Clear();

            bool isKbm = OutputType == VirtualControllerType.KeyboardMouse;
            if (isKbm)
            {
                // KBM: stick 0 = Mouse X/Y, stick 1 = Scroll Wheel (Y-axis only)
                var mouse = new StickConfigItem(0, Strings.Instance.Pad_MouseMovement, -1, -1);
                SyncStickItemFromVm(mouse);
                mouse.PropertyChanged += OnStickConfigPropertyChanged;
                StickConfigs.Add(mouse);

                var scroll = new StickConfigItem(1, Strings.Instance.Stick_ScrollWheel, -1, -1);
                SyncStickItemFromVm(scroll);
                scroll.PropertyChanged += OnStickConfigPropertyChanged;
                StickConfigs.Add(scroll);
                return;
            }

            // Xbox / PlayStation use a fixed 2-stick gamepad grid;
            // Extended takes its stick count from the active HIDMaestro
            // profile via ExtendedConfig.
            int count = 2;
            bool isExtended = OutputType == VirtualControllerType.Extended;
            if (isExtended)
                count = ExtendedConfig.ThumbstickCount;

            int[] axX = null, axY = null, trAx = null;
            if (isExtended && count > 0)
                ExtendedConfig.ComputeAxisLayout(out axX, out axY, out trAx);

            for (int i = 0; i < count; i++)
            {
                string title = isExtended
                    ? string.Format(Strings.Instance.Stick_Format, i + 1)
                    : i == 0 ? Strings.Instance.Stick_LeftThumbstick : Strings.Instance.Stick_RightThumbstick;
                int xiIdx = axX != null ? axX[i] : -1;
                int yiIdx = axY != null ? axY[i] : -1;
                string iconLabel = isExtended
                    ? (i + 1).ToString()
                    : i == 0 ? "L" : "R";
                // Boundary calibration for the two primary thumbsticks only
                // (#174): gamepad L/R and Extended sticks 0/1, which the Step 3
                // warp covers. Extended custom sticks 2+ are deferred.
                var item = new StickConfigItem(i, title, xiIdx, yiIdx, iconLabel,
                    supportsBoundaryCalibration: i <= 1);
                SyncStickItemFromVm(item);
                item.PropertyChanged += OnStickConfigPropertyChanged;
                StickConfigs.Add(item);
            }
            // Steering isn't in SyncStickItemFromVm, so the fresh items default to mode-off.
            // Re-load the selected device's steering into them (host wires this).
            SteeringReloadCallback?.Invoke();
        }

        /// <summary>
        /// Rebuilds the TriggerConfigs collection based on the current output type.
        /// For Xbox / PlayStation (or Extended with gamepad preset): always 2 triggers (Left, Right).
        /// For Extended Custom: N triggers based on TriggerCount.
        /// </summary>
        public void RebuildTriggerConfigs()
        {
            foreach (var item in TriggerConfigs)
                item.PropertyChanged -= OnTriggerConfigPropertyChanged;
            TriggerConfigs.Clear();

            // KBM has no triggers — scroll is on Right Stick Y.
            if (OutputType == VirtualControllerType.KeyboardMouse)
                return;

            // Xbox / PlayStation use a fixed 2-trigger gamepad grid;
            // Extended takes its trigger count from the active HIDMaestro
            // profile via ExtendedConfig.
            int count = 2;
            bool isExtended = OutputType == VirtualControllerType.Extended;
            if (isExtended)
                count = ExtendedConfig.TriggerCount;

            int[] axX = null, axY = null, trAx = null;
            if (isExtended && count > 0)
                ExtendedConfig.ComputeAxisLayout(out axX, out axY, out trAx);

            bool isPlayStation = OutputType == VirtualControllerType.PlayStation;
            for (int i = 0; i < count; i++)
            {
                string title = isExtended
                    ? string.Format(Strings.Instance.Trigger_Format, i + 1)
                    : i == 0 ? Strings.Instance.Btn_LeftTrigger : Strings.Instance.Btn_RightTrigger;
                int ai = trAx != null ? trAx[i] : -1;
                string iconLabel;
                bool iconRightSide;
                if (isExtended)
                {
                    iconLabel = (i + 1).ToString();
                    iconRightSide = (i % 2) == 1;
                }
                else if (isPlayStation)
                {
                    iconLabel = i == 0 ? "L2" : "R2";
                    iconRightSide = i == 1;
                }
                else
                {
                    iconLabel = i == 0 ? "LT" : "RT";
                    iconRightSide = i == 1;
                }
                var item = new TriggerConfigItem(i, title, ai, iconLabel, iconRightSide);
                SyncTriggerItemFromVm(item);
                item.PropertyChanged += OnTriggerConfigPropertyChanged;
                TriggerConfigs.Add(item);
            }
        }

        /// <summary>
        /// Pushes current VM deadzone properties into a StickConfigItem.
        /// Called on rebuild and when settings are loaded.
        /// </summary>
        public void SyncStickItemFromVm(StickConfigItem item)
        {
            _syncingConfigItems = true;
            try
            {
                switch (item.Index)
                {
                    case 0:
                        item.DeadZoneShape = (DeadZoneShape)LeftDeadZoneShape;
                        item.DeadZoneX = LeftDeadZoneX;
                        item.DeadZoneY = LeftDeadZoneY;
                        item.AntiDeadZoneX = LeftAntiDeadZoneX;
                        item.AntiDeadZoneY = LeftAntiDeadZoneY;
                        item.Linear = LeftLinear;
                        item.SensitivityCurveX = LeftSensitivityCurveX;
                        item.SensitivityCurveY = LeftSensitivityCurveY;
                        item.MaxRangeX = LeftMaxRangeX;
                        item.MaxRangeY = LeftMaxRangeY;
                        item.MaxRangeXNeg = LeftMaxRangeXNeg;
                        item.MaxRangeYNeg = LeftMaxRangeYNeg;
                        item.CenterOffsetX = LeftCenterOffsetX;
                        item.CenterOffsetY = LeftCenterOffsetY;
                        item.BoundaryMap = LeftThumbBoundaryMap;
                        break;
                    case 1:
                        item.DeadZoneShape = (DeadZoneShape)RightDeadZoneShape;
                        item.DeadZoneX = RightDeadZoneX;
                        item.DeadZoneY = RightDeadZoneY;
                        item.AntiDeadZoneX = RightAntiDeadZoneX;
                        item.AntiDeadZoneY = RightAntiDeadZoneY;
                        item.Linear = RightLinear;
                        item.SensitivityCurveX = RightSensitivityCurveX;
                        item.SensitivityCurveY = RightSensitivityCurveY;
                        item.MaxRangeX = RightMaxRangeX;
                        item.MaxRangeY = RightMaxRangeY;
                        item.MaxRangeXNeg = RightMaxRangeXNeg;
                        item.MaxRangeYNeg = RightMaxRangeYNeg;
                        item.CenterOffsetX = RightCenterOffsetX;
                        item.CenterOffsetY = RightCenterOffsetY;
                        item.BoundaryMap = RightThumbBoundaryMap;
                        break;
                }
            }
            finally { _syncingConfigItems = false; }
        }

        /// <summary>
        /// Pushes current VM trigger properties into a TriggerConfigItem.
        /// </summary>
        public void SyncTriggerItemFromVm(TriggerConfigItem item)
        {
            _syncingConfigItems = true;
            try
            {
                switch (item.Index)
                {
                    case 0:
                        item.DeadZone = LeftTriggerDeadZone;
                        item.MaxRange = LeftTriggerMaxRange;
                        item.AntiDeadZone = LeftTriggerAntiDeadZone;
                        item.SensitivityCurve = LeftTriggerSensitivityCurve;
                        break;
                    case 1:
                        item.DeadZone = RightTriggerDeadZone;
                        item.MaxRange = RightTriggerMaxRange;
                        item.AntiDeadZone = RightTriggerAntiDeadZone;
                        item.SensitivityCurve = RightTriggerSensitivityCurve;
                        break;
                }
            }
            finally { _syncingConfigItems = false; }
        }

        /// <summary>
        /// Syncs all StickConfigItem values back from current VM properties.
        /// Called after settings are loaded/pasted.
        /// </summary>
        public void SyncAllConfigItemsFromVm()
        {
            foreach (var item in StickConfigs)
                SyncStickItemFromVm(item);
            foreach (var item in TriggerConfigs)
                SyncTriggerItemFromVm(item);
        }

        /// <summary>Invoked at the end of <see cref="RebuildStickConfigs"/> so the host can
        /// re-load the selected assigned device's steering into the freshly-rebuilt items
        /// (rebuild resets steering to defaults and SyncStickItemFromVm doesn't cover it).</summary>
        public System.Action SteeringReloadCallback { get; set; }

        /// <summary>Loads each stick's steering mode + tunables from the SELECTED assigned
        /// device's stored values, with the dirty callback suppressed (mirrors the guarded
        /// deadzone sync). Steering is per assigned device, so this runs on every device
        /// select and after a rebuild. <paramref name="get"/> resolves a steering key
        /// (e.g. "Stick0SteerKind") for the selected device. Steering has no flat VM mirror
        /// property, so it loads straight onto the items.</summary>
        public void LoadSteeringConfigItems(System.Func<string, string> get)
        {
            if (get == null) return;
            bool prev = _syncingConfigItems;
            _syncingConfigItems = true;
            try
            {
                foreach (var stick in StickConfigs)
                {
                    int g = stick.Index;
                    if (g < 0) continue;
                    stick.SetSteeringKind(get($"Stick{g}SteerKind"));
                    stick.WindRangeDeg = ParseSteerDouble(get($"Stick{g}SteerWindRange"), 900);
                    stick.WindPower = ParseSteerDouble(get($"Stick{g}SteerWindPower"), 1);
                    stick.WindUnwindRate = ParseSteerDouble(get($"Stick{g}SteerWindUnwind"), 1800);
                    stick.AngleInnerDz = ParseSteerDouble(get($"Stick{g}SteerAngleInner"), 0);
                    stick.AngleOuterDz = ParseSteerDouble(get($"Stick{g}SteerAngleOuter"), 10);
                    // Motion-lean tuning moved to Motion Steering (loaded separately).
                }
            }
            finally { _syncingConfigItems = prev; }
        }

        private static double ParseSteerDouble(string s, double dflt)
            => double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : dflt;

        // Persisted stick-config property names. PropertyChanged on any other
        // property (LiveX/LiveY/RawX/RawY/LiveInputX/LiveInputY/IsCalibrating
        // and computed display siblings) is a per-frame UI update, not a
        // user-config change — must not flag the document dirty.
        private static readonly System.Collections.Generic.HashSet<string> StickConfigPropertyNames = new()
        {
            nameof(StickConfigItem.DeadZoneShape),
            nameof(StickConfigItem.DeadZoneX), nameof(StickConfigItem.DeadZoneY),
            nameof(StickConfigItem.AntiDeadZoneX), nameof(StickConfigItem.AntiDeadZoneY),
            nameof(StickConfigItem.Linear),
            nameof(StickConfigItem.SensitivityCurveX), nameof(StickConfigItem.SensitivityCurveY),
            nameof(StickConfigItem.MaxRangeX), nameof(StickConfigItem.MaxRangeY),
            nameof(StickConfigItem.MaxRangeXNeg), nameof(StickConfigItem.MaxRangeYNeg),
            nameof(StickConfigItem.CenterOffsetX), nameof(StickConfigItem.CenterOffsetY),
            nameof(StickConfigItem.BoundaryMap), // #174 boundary calibration
            // Steering (#94). Without these, changing the steering mode / tunables
            // never marks the profile dirty, so the selection is dropped on save.
            nameof(StickConfigItem.SteeringModeIndex),
            nameof(StickConfigItem.WindRangeDeg), nameof(StickConfigItem.WindPower), nameof(StickConfigItem.WindUnwindRate),
            nameof(StickConfigItem.AngleInnerDz), nameof(StickConfigItem.AngleOuterDz),
        };

        private static readonly System.Collections.Generic.HashSet<string> TriggerConfigPropertyNames = new()
        {
            nameof(TriggerConfigItem.DeadZone),
            nameof(TriggerConfigItem.MaxRange),
            nameof(TriggerConfigItem.AntiDeadZone),
            nameof(TriggerConfigItem.SensitivityCurve),
        };

        private void OnStickConfigPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_syncingConfigItems) return;
            if (sender is not StickConfigItem item) return;

            bool isConfigProp = e.PropertyName != null && StickConfigPropertyNames.Contains(e.PropertyName);

            // Sync changed property back to VM
            switch (item.Index)
            {
                case 0:
                    switch (e.PropertyName)
                    {
                        case nameof(StickConfigItem.DeadZoneShape): LeftDeadZoneShape = (int)item.DeadZoneShape; break;
                        case nameof(StickConfigItem.DeadZoneX): LeftDeadZoneX = item.DeadZoneX; break;
                        case nameof(StickConfigItem.DeadZoneY): LeftDeadZoneY = item.DeadZoneY; break;
                        case nameof(StickConfigItem.AntiDeadZoneX): LeftAntiDeadZoneX = item.AntiDeadZoneX; break;
                        case nameof(StickConfigItem.AntiDeadZoneY): LeftAntiDeadZoneY = item.AntiDeadZoneY; break;
                        case nameof(StickConfigItem.Linear): LeftLinear = item.Linear; break;
                        case nameof(StickConfigItem.SensitivityCurveX): LeftSensitivityCurveX = item.SensitivityCurveX; break;
                        case nameof(StickConfigItem.SensitivityCurveY): LeftSensitivityCurveY = item.SensitivityCurveY; break;
                        case nameof(StickConfigItem.MaxRangeX): LeftMaxRangeX = item.MaxRangeX; break;
                        case nameof(StickConfigItem.MaxRangeY): LeftMaxRangeY = item.MaxRangeY; break;
                        case nameof(StickConfigItem.MaxRangeXNeg): LeftMaxRangeXNeg = item.MaxRangeXNeg; break;
                        case nameof(StickConfigItem.MaxRangeYNeg): LeftMaxRangeYNeg = item.MaxRangeYNeg; break;
                        case nameof(StickConfigItem.CenterOffsetX): LeftCenterOffsetX = item.CenterOffsetX; break;
                        case nameof(StickConfigItem.CenterOffsetY): LeftCenterOffsetY = item.CenterOffsetY; break;
                        case nameof(StickConfigItem.BoundaryMap): LeftThumbBoundaryMap = item.BoundaryMap; break;
                    }
                    if (isConfigProp) ConfigItemDirtyCallback?.Invoke();
                    break;
                case 1:
                    switch (e.PropertyName)
                    {
                        case nameof(StickConfigItem.DeadZoneShape): RightDeadZoneShape = (int)item.DeadZoneShape; break;
                        case nameof(StickConfigItem.DeadZoneX): RightDeadZoneX = item.DeadZoneX; break;
                        case nameof(StickConfigItem.DeadZoneY): RightDeadZoneY = item.DeadZoneY; break;
                        case nameof(StickConfigItem.AntiDeadZoneX): RightAntiDeadZoneX = item.AntiDeadZoneX; break;
                        case nameof(StickConfigItem.AntiDeadZoneY): RightAntiDeadZoneY = item.AntiDeadZoneY; break;
                        case nameof(StickConfigItem.Linear): RightLinear = item.Linear; break;
                        case nameof(StickConfigItem.SensitivityCurveX): RightSensitivityCurveX = item.SensitivityCurveX; break;
                        case nameof(StickConfigItem.SensitivityCurveY): RightSensitivityCurveY = item.SensitivityCurveY; break;
                        case nameof(StickConfigItem.MaxRangeX): RightMaxRangeX = item.MaxRangeX; break;
                        case nameof(StickConfigItem.MaxRangeY): RightMaxRangeY = item.MaxRangeY; break;
                        case nameof(StickConfigItem.MaxRangeXNeg): RightMaxRangeXNeg = item.MaxRangeXNeg; break;
                        case nameof(StickConfigItem.MaxRangeYNeg): RightMaxRangeYNeg = item.MaxRangeYNeg; break;
                        case nameof(StickConfigItem.CenterOffsetX): RightCenterOffsetX = item.CenterOffsetX; break;
                        case nameof(StickConfigItem.CenterOffsetY): RightCenterOffsetY = item.CenterOffsetY; break;
                        case nameof(StickConfigItem.BoundaryMap): RightThumbBoundaryMap = item.BoundaryMap; break;
                    }
                    if (isConfigProp) ConfigItemDirtyCallback?.Invoke();
                    break;
                default:
                    // Extended custom sticks 2+: values stored directly on ConfigItem,
                    // persisted via SettingsService.UpdatePadSettingsFromViewModels.
                    if (isConfigProp) ConfigItemDirtyCallback?.Invoke();
                    break;
            }

            // A steering-mode switch (including Reset all setting it back to Direct) must
            // re-stamp the engine's MappingSet now, not on the 2s autosave, or the stick
            // keeps steering after the reset / mode change.
            if (e.PropertyName == nameof(StickConfigItem.SteeringModeIndex))
                SteeringModeChangedCallback?.Invoke();
        }

        private void OnTriggerConfigPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_syncingConfigItems) return;
            if (sender is not TriggerConfigItem item) return;

            bool isConfigProp = e.PropertyName != null && TriggerConfigPropertyNames.Contains(e.PropertyName);

            switch (item.Index)
            {
                case 0:
                    switch (e.PropertyName)
                    {
                        case nameof(TriggerConfigItem.DeadZone): LeftTriggerDeadZone = item.DeadZone; break;
                        case nameof(TriggerConfigItem.MaxRange): LeftTriggerMaxRange = item.MaxRange; break;
                        case nameof(TriggerConfigItem.AntiDeadZone): LeftTriggerAntiDeadZone = item.AntiDeadZone; break;
                        case nameof(TriggerConfigItem.SensitivityCurve): LeftTriggerSensitivityCurve = item.SensitivityCurve; break;
                    }
                    if (isConfigProp) ConfigItemDirtyCallback?.Invoke();
                    break;
                case 1:
                    switch (e.PropertyName)
                    {
                        case nameof(TriggerConfigItem.DeadZone): RightTriggerDeadZone = item.DeadZone; break;
                        case nameof(TriggerConfigItem.MaxRange): RightTriggerMaxRange = item.MaxRange; break;
                        case nameof(TriggerConfigItem.AntiDeadZone): RightTriggerAntiDeadZone = item.AntiDeadZone; break;
                        case nameof(TriggerConfigItem.SensitivityCurve): RightTriggerSensitivityCurve = item.SensitivityCurve; break;
                    }
                    if (isConfigProp) ConfigItemDirtyCallback?.Invoke();
                    break;
                default:
                    // Extended custom triggers 2+: values stored directly on ConfigItem,
                    // persisted via SettingsService.UpdatePadSettingsFromViewModels.
                    if (isConfigProp) ConfigItemDirtyCallback?.Invoke();
                    break;
            }
        }

        // ═══════════════════════════════════════════════
        //  #4: Macro system — foundation
        // ═══════════════════════════════════════════════

        /// <summary>Macros configured for this pad slot.</summary>
        public ObservableCollection<MacroItem> Macros { get; } = new();

        private MacroItem _selectedMacro;

        public MacroItem SelectedMacro
        {
            get => _selectedMacro;
            set
            {
                if (SetProperty(ref _selectedMacro, value))
                {
                    OnPropertyChanged(nameof(HasSelectedMacro));
                    _removeMacroCommand?.NotifyCanExecuteChanged();
                    // CommunityToolkit RelayCommand doesn't auto-requery, so the
                    // selection-gated macro commands stay disabled until told (#112).
                    _duplicateMacroCommand?.NotifyCanExecuteChanged();
                    _copyMacroCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        public bool HasSelectedMacro => _selectedMacro != null;

        private RelayCommand _addMacroCommand;
        public RelayCommand AddMacroCommand =>
            _addMacroCommand ??= new RelayCommand(() =>
            {
                var macro = new MacroItem
                {
                    PadIndex = PadIndex,
                    Name = $"Macro {Macros.Count + 1}",
                    ButtonStyle = MacroButtonNames.DeriveStyle(_outputType),
                    ExtendedProfileId = SlotExtendedProfileId
                };
                Macros.Add(macro);
                SelectedMacro = macro;
            });

        private RelayCommand _removeMacroCommand;
        public RelayCommand RemoveMacroCommand =>
            _removeMacroCommand ??= new RelayCommand(() =>
            {
                if (_selectedMacro != null)
                {
                    Macros.Remove(_selectedMacro);
                    SelectedMacro = Macros.LastOrDefault();
                }
            }, () => HasSelectedMacro);

        private RelayCommand _duplicateMacroCommand;
        /// <summary>Duplicates the selected macro within this slot (#112). The clone
        /// round-trips through the macro DTO, so it is a deep copy and the runtime-only
        /// execution state (IsExecuting, CurrentActionIndex, ...) drops away. A macro
        /// that is mid-fire keeps firing to completion; the clone starts cold.</summary>
        public RelayCommand DuplicateMacroCommand =>
            _duplicateMacroCommand ??= new RelayCommand(() =>
            {
                if (_selectedMacro == null) return;
                var data = SettingsService.BuildMacroDataForMacro(_selectedMacro, PadIndex);
                var clone = SettingsService.LoadMacroFromData(data, OutputType, ExtendedConfig?.ButtonCount, ProfileId);
                clone.PadIndex = PadIndex;
                clone.Name = string.Format(Strings.Instance.Macro_CopyNameFormat, _selectedMacro.Name);
                Macros.Add(clone);
                SelectedMacro = clone;
            }, () => HasSelectedMacro);

        // Macro clipboard (#112). The View layer (MainWindow) owns the clipboard
        // and the device picker, so these mirror the slot Copy / Paste / Copy From
        // triplet: the command raises an event, MainWindow does the work.
        public event EventHandler CopyMacroRequested;
        private RelayCommand _copyMacroCommand;
        public RelayCommand CopyMacroCommand =>
            _copyMacroCommand ??= new RelayCommand(
                () => CopyMacroRequested?.Invoke(this, EventArgs.Empty),
                () => HasSelectedMacro);

        public event EventHandler PasteMacroRequested;
        private RelayCommand _pasteMacroCommand;
        public RelayCommand PasteMacroCommand =>
            _pasteMacroCommand ??= new RelayCommand(
                () => PasteMacroRequested?.Invoke(this, EventArgs.Empty));

        /// <summary>VC-level Copy From (#112): pull macros from another virtual
        /// controller into this one, mirroring the Mappings-tab Copy From. MainWindow
        /// shows the VC picker and does the copy.</summary>
        public event EventHandler CopyMacroFromRequested;
        private RelayCommand _copyMacroFromCommand;
        public RelayCommand CopyMacroFromCommand =>
            _copyMacroFromCommand ??= new RelayCommand(
                () => CopyMacroFromRequested?.Invoke(this, EventArgs.Empty));

        // ═══════════════════════════════════════════════
        //  Radial / touch menus (#9 B-17). Slot-level, like Macros.
        //  The VM wraps the LIVE MenuDefinitionEntry list on this slot's
        //  MappingSet (write-through); every edit fires
        //  ConfigItemDirtyCallback so the autosave path persists the set.
        // ═══════════════════════════════════════════════

        /// <summary>Menus configured for this pad slot.</summary>
        public ObservableCollection<MenuEditorItem> Menus { get; } = new();

        /// <summary>
        /// Callback invoked after a structural menu change (add / remove /
        /// duplicate, kind, cell count, center cell, enabled) that changes
        /// which "Menu N Item K" descriptors exist. Wired by MainWindow to
        /// InputService.RefreshAvailableInputsForSlot so the Mappings and
        /// macro pickers stop offering stale entries. Label / name typing
        /// must NOT invoke it.
        /// </summary>
        public Action MenusStructureChanged { get; set; }

        private MenuEditorItem _selectedMenu;

        public MenuEditorItem SelectedMenu
        {
            get => _selectedMenu;
            set
            {
                if (SetProperty(ref _selectedMenu, value))
                {
                    OnPropertyChanged(nameof(HasSelectedMenu));
                    _removeMenuCommand?.NotifyCanExecuteChanged();
                    _duplicateMenuCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        public bool HasSelectedMenu => _selectedMenu != null;

        private PadForge.Engine.Data.MappingSet SlotMenuSet
        {
            get
            {
                var sets = PadForge.Common.Input.SettingsManager.SlotMappingSets;
                if (sets == null || PadIndex < 0 || PadIndex >= sets.Length) return null;
                return sets[PadIndex] ??= new PadForge.Engine.Data.MappingSet();
            }
        }

        /// <summary>Rebuilds the Menus collection from this slot's live
        /// MappingSet. Called from <see cref="RebuildMappings"/> so profile
        /// applies, Workshop imports, and output-type changes all refresh
        /// the tab (the reset-mirror leg: Reset to Defaults replaces the
        /// slot sets and lands here too).</summary>
        public void ReloadMenus()
        {
            Menus.Clear();
            var set = SlotMenuSet;
            if (set?.Menus != null)
            {
                foreach (var entry in set.Menus)
                {
                    if (entry == null) continue;
                    var vm = new MenuEditorItem(entry);
                    ApplyMenuButtonStyle(vm);
                    vm.Changed += OnMenuEdited;
                    Menus.Add(vm);
                }
            }
            SelectedMenu = Menus.Count > 0 ? Menus[0] : null;
        }

        private void OnMenuEdited() => ConfigItemDirtyCallback?.Invoke();

        private RelayCommand _addMenuCommand;
        public RelayCommand AddMenuCommand =>
            _addMenuCommand ??= new RelayCommand(() =>
            {
                var set = SlotMenuSet;
                if (set == null) return;
                int id = 1;
                foreach (var m in set.Menus)
                    if (m != null && m.MenuId >= id) id = m.MenuId + 1;
                var entry = new PadForge.Engine.Menus.MenuDefinitionEntry
                {
                    MenuId = id,
                    Name = string.Format(Strings.Instance.Menu_NewNameFormat, id),
                };
                set.Menus.Add(entry);
                var vm = new MenuEditorItem(entry);
                ApplyMenuButtonStyle(vm);
                vm.Changed += OnMenuEdited;
                Menus.Add(vm);
                SelectedMenu = vm;
                OnMenuEdited();
                MenusStructureChanged?.Invoke();
            });

        private RelayCommand _removeMenuCommand;
        public RelayCommand RemoveMenuCommand =>
            _removeMenuCommand ??= new RelayCommand(() =>
            {
                var vm = _selectedMenu;
                if (vm == null) return;
                SlotMenuSet?.Menus.Remove(vm.Entry);
                Menus.Remove(vm);
                SelectedMenu = Menus.LastOrDefault();
                OnMenuEdited();
                MenusStructureChanged?.Invoke();
            }, () => HasSelectedMenu);

        private RelayCommand _duplicateMenuCommand;
        public RelayCommand DuplicateMenuCommand =>
            _duplicateMenuCommand ??= new RelayCommand(() =>
            {
                var vm = _selectedMenu;
                var set = SlotMenuSet;
                if (vm == null || set == null) return;
                var clone = vm.Entry.Clone();
                int id = 1;
                foreach (var m in set.Menus)
                    if (m != null && m.MenuId >= id) id = m.MenuId + 1;
                clone.MenuId = id;
                clone.Name = string.Format(Strings.Instance.Macro_CopyNameFormat, vm.Entry.Name);
                set.Menus.Add(clone);
                var cloneVm = new MenuEditorItem(clone);
                ApplyMenuButtonStyle(cloneVm);
                cloneVm.Changed += OnMenuEdited;
                Menus.Add(cloneVm);
                SelectedMenu = cloneVm;
                OnMenuEdited();
                MenusStructureChanged?.Invoke();
            }, () => HasSelectedMenu);

        /// <summary>Record button on the menu host picker: MainWindow runs
        /// the freeform recorder and folds the recorded descriptor onto a
        /// host choice, the Aim Engage record shape.</summary>
        public event EventHandler MenuHostRecordRequested;
        private RelayCommand _menuHostRecordCommand;
        public RelayCommand MenuHostRecordCommand =>
            _menuHostRecordCommand ??= new RelayCommand(() =>
            {
                SelectedMenu?.PrepareRecord(MenuEditorItem.MenuRecordTarget.Host);
                MenuHostRecordRequested?.Invoke(this, EventArgs.Empty);
            });

        /// <summary>Record buttons for the Custom opener's steer axes and
        /// the Click input: same freeform recorder, aimed by parameter
        /// ("CustomX" / "CustomY" / "Click").</summary>
        public CommunityToolkit.Mvvm.Input.RelayCommand<string> MenuRecordTargetCommand =>
            _menuRecordTargetCommand ??= new CommunityToolkit.Mvvm.Input.RelayCommand<string>(p =>
            {
                var menu = SelectedMenu;
                if (menu == null) return;
                if (!Enum.TryParse<MenuEditorItem.MenuRecordTarget>(p, out var target)) return;
                menu.PrepareRecord(target);
                MenuHostRecordRequested?.Invoke(this, EventArgs.Empty);
            });
        private CommunityToolkit.Mvvm.Input.RelayCommand<string> _menuRecordTargetCommand;

        // ═══════════════════════════════════════════════
        //  Audio tab (issue #83) — per-slot sound output for macro sounds
        // ═══════════════════════════════════════════════

        /// <summary>True when the SELECTED assigned device has a built-in
        /// speaker (DualSense / Edge / DualShock 4). The Audio tab is per
        /// assigned device by convention — this gates the mirror toggle and
        /// the routing notes.</summary>
        public bool SelectedDeviceHasSpeaker
        {
            get
            {
                var sel = SelectedMappedDevice;
                if (sel == null || sel.InstanceGuid == Guid.Empty) return false;
                var ud = PadForge.Common.Input.SettingsManager.FindDeviceByInstanceGuid(sel.InstanceGuid);
                if (ud == null) return false;
                // Wii Remote built-in speaker (#146): Nintendo VID 0x057E,
                // RVL-CNT-01 (0x0306) / -TR (0x0330).
                if (PadForge.Common.Input.WiiSpeakerService.DeviceHasSpeaker(ud)) return true;
                // Switch HD Rumble / Steam Controller haptic tones (#147):
                // Joy-Con L/R, Switch Pro, Steam Controller 2015.
                if (PadForge.Common.Input.HapticToneService.DeviceHasHaptics(ud)) return true;
                if (ud.VendorId != 0x054C) return false;
                // DS5 family: audio on both transports. DS4: audio is
                // Bluetooth-only (wired DS4 exposes no USB audio interface);
                // Sony's USB wireless adaptor (0x0BA0) tunnels the radio link
                // and provides real USB audio endpoints.
                if (ud.ProdId is 0x0CE6 or 0x0DF2 or 0x0BA0) return true;
                if (ud.ProdId is 0x05C4 or 0x09CC)
                    return (ud.DevicePath ?? "").IndexOf("{00001124", StringComparison.OrdinalIgnoreCase) >= 0;
                return false;
            }
        }

        public bool SelectedDeviceHasNoSpeaker => !SelectedDeviceHasSpeaker;

        /// <summary>True when the SELECTED assigned device plays haptic TONES
        /// (Joy-Con, Switch Pro, Steam family) rather than real speaker audio.
        /// Gates the mirror engage row (#185): the engage gate exists because a
        /// resonant actuator buzzing with music is intrusive in a way a real
        /// speaker is not, so Sony / Wii speaker devices do not show it.</summary>
        public bool SelectedDeviceHasHapticTones
        {
            get
            {
                var sel = SelectedMappedDevice;
                if (sel == null || sel.InstanceGuid == Guid.Empty) return false;
                var ud = PadForge.Common.Input.SettingsManager.FindDeviceByInstanceGuid(sel.InstanceGuid);
                return ud != null && PadForge.Common.Input.HapticToneService.DeviceHasHaptics(ud);
            }
        }

        /// <summary>True when the SELECTED assigned device has a touchpad
        /// AND a haptic lane the swipe-tick feature can drive (Steam
        /// Controller family actuators, or a dispatcher-driven Sony pad).
        /// Gates the Touchpad tab's Swipe Haptics card (#219) so devices
        /// whose touchpads have no haptics (Precision Touchpads, the
        /// overlay, web controllers) don't show a dead toggle.</summary>
        public bool SelectedDeviceHasTouchpadPulse
        {
            get
            {
                var sel = SelectedMappedDevice;
                if (sel == null || sel.InstanceGuid == Guid.Empty) return false;
                var ud = PadForge.Common.Input.SettingsManager.FindDeviceByInstanceGuid(sel.InstanceGuid);
                return PadForge.Common.Input.TouchpadPulseService.DeviceHasSwipePulse(ud);
            }
        }

        /// <summary>InputChoice projection over the selected device's
        /// AudioMirrorEngageButton + AudioMirrorEngageDeviceGuid pair (#185),
        /// mirroring <see cref="GyroAimEngageSelectedInput"/>: the getter
        /// resolves the matching entry in <see cref="SlotAvailableInputs"/>,
        /// the setter writes both backing strings atomically, and a null
        /// write-back from the ComboBox is ignored because the getter returns
        /// null any time the list has not been rebuilt yet for the newly
        /// selected device. Treating that transient null as a user clear
        /// would silently wipe the saved binding on every device switch.</summary>
        public InputChoice MirrorEngageSelectedInput
        {
            get
            {
                var cfg = DeviceConfig;
                if (cfg == null || string.IsNullOrEmpty(cfg.AudioMirrorEngageButton)) return null;
                foreach (var c in SlotAvailableInputs)
                {
                    if (c == null) continue;
                    if (string.Equals(c.Descriptor, cfg.AudioMirrorEngageButton, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(c.DeviceGuid ?? "", cfg.AudioMirrorEngageDeviceGuid ?? "", StringComparison.OrdinalIgnoreCase))
                        return c;
                }
                return null;
            }
            set
            {
                if (value == null) return;
                var cfg = DeviceConfig;
                if (cfg == null) return;
                cfg.AudioMirrorEngageButton = value.Descriptor ?? "";
                cfg.AudioMirrorEngageDeviceGuid = value.DeviceGuid ?? "";
                OnPropertyChanged(nameof(MirrorEngageSelectedInput));
            }
        }

        /// <summary>Re-resolves <see cref="MirrorEngageSelectedInput"/> after
        /// InputService.PopulateAvailableInputs rebuilds the slot's
        /// cross-device input list, the same hook the gyro picker needs.</summary>
        public void OnMirrorEngageSelectedInputRefresh()
            => OnPropertyChanged(nameof(MirrorEngageSelectedInput));

        // ── #185 engage row: record + per-setting resets, the same cluster
        //    every input picker ships (gyro Aim Engage, trigger-route
        //    activators). Record toggles a freeform recorder session handled
        //    by MainWindow; each reset restores one setting's default. ──

        private bool _mirrorEngageRecording;
        /// <summary>Whether the engage-input recorder is listening for the
        /// next physical input. Drives the record button's icon + tooltip
        /// swap, mirroring <see cref="GyroAimEngageRecording"/>.</summary>
        public bool MirrorEngageRecording
        {
            get => _mirrorEngageRecording;
            set
            {
                if (SetProperty(ref _mirrorEngageRecording, value))
                {
                    OnPropertyChanged(nameof(MirrorEngageRecordButtonIcon));
                    OnPropertyChanged(nameof(MirrorEngageRecordButtonText));
                }
            }
        }
        /// <summary>Segoe MDL2 glyph: Stop while recording, Record while
        /// idle. Same literals as the gyro twin.</summary>
        public string MirrorEngageRecordButtonIcon => _mirrorEngageRecording ? "" : "";
        /// <summary>Localized tooltip for the record button.</summary>
        public string MirrorEngageRecordButtonText => _mirrorEngageRecording
            ? Strings.Instance.Common_Recording
            : Strings.Instance.Common_Record;

        /// <summary>Fires when the user clicks the Record button next to the
        /// engage-input picker. MainWindow starts or cancels a freeform
        /// recorder session, matching the Aim Engage pattern.</summary>
        public event EventHandler MirrorEngageRecordRequested;
        public void FireMirrorEngageRecord() => MirrorEngageRecordRequested?.Invoke(this, EventArgs.Empty);

        private RelayCommand _mirrorEngageRecordCommand;
        public RelayCommand MirrorEngageRecordCommand =>
            _mirrorEngageRecordCommand ??= new RelayCommand(FireMirrorEngageRecord);

        private RelayCommand _resetMirrorEngageModeCommand;
        public RelayCommand ResetMirrorEngageModeCommand =>
            _resetMirrorEngageModeCommand ??= new RelayCommand(() =>
            {
                if (DeviceConfig != null) DeviceConfig.AudioMirrorEngageMode = "Always";
            });

        private RelayCommand _resetMirrorEngageInputCommand;
        /// <summary>The sanctioned clear path for the engage input (the
        /// picker's null-writeback guard exists because of it).</summary>
        public RelayCommand ResetMirrorEngageInputCommand =>
            _resetMirrorEngageInputCommand ??= new RelayCommand(() =>
            {
                if (DeviceConfig == null) return;
                DeviceConfig.AudioMirrorEngageButton = string.Empty;
                DeviceConfig.AudioMirrorEngageDeviceGuid = string.Empty;
                OnPropertyChanged(nameof(MirrorEngageSelectedInput));
            });

        private RelayCommand _resetMirrorEngageReleaseMsCommand;
        public RelayCommand ResetMirrorEngageReleaseMsCommand =>
            _resetMirrorEngageReleaseMsCommand ??= new RelayCommand(() =>
            {
                if (DeviceConfig != null) DeviceConfig.AudioMirrorEngageReleaseMs = 500;
            });

        private RelayCommand _resetToneFilterModeCommand;
        public RelayCommand ResetToneFilterModeCommand =>
            _resetToneFilterModeCommand ??= new RelayCommand(() =>
            {
                if (DeviceConfig != null) DeviceConfig.AudioToneFilterMode = "Off";
            });

        private RelayCommand _resetToneLimitHzCommand;
        public RelayCommand ResetToneLimitHzCommand =>
            _resetToneLimitHzCommand ??= new RelayCommand(() =>
            {
                if (DeviceConfig != null) DeviceConfig.AudioToneLimitHz = 800;
            });

        private RelayCommand _resetMirrorSourceCommand;
        /// <summary>Clears the mirror source back to the system default
        /// endpoint (Id ""), the same value the Sound Output "Reset All"
        /// path writes.</summary>
        public RelayCommand ResetMirrorSourceCommand =>
            _resetMirrorSourceCommand ??= new RelayCommand(() => SelectedMirrorSourceId = string.Empty);

        private int _irSensorBarPos;
        /// <summary>Sensor-bar position for the IR pointer (issue #146): 0 =
        /// centered, 1 = above the screen, 2 = below. Per (device, slot) on
        /// PadSetting like every other pad-page tunable, so two virtual
        /// controllers sharing one remote keep independent pointer feel. Read
        /// at runtime through SourceCoercion.IrTuningProvider.</summary>
        public int IrSensorBarPos
        {
            get => _irSensorBarPos;
            set => SetProperty(ref _irSensorBarPos, Math.Clamp(value, 0, 2));
        }

        private int _irSensorBarCompPercent;
        /// <summary>Sensor-bar vertical-offset magnitude, shown as 0..50 percent
        /// of the pointer range and stored as 0..0.5 on
        /// <see cref="PadSetting.IrSensorBarComp"/> (issue #146).</summary>
        public int IrSensorBarCompPercent
        {
            get => _irSensorBarCompPercent;
            set => SetProperty(ref _irSensorBarCompPercent, Math.Clamp(value, 0, 50));
        }

        private int _irSmoothingPercent;
        /// <summary>IR pointer smoothing, shown as 0..100 percent and stored as
        /// 0..1 on <see cref="PadSetting.IrSmoothing"/> (issue #146).</summary>
        public int IrSmoothingPercent
        {
            get => _irSmoothingPercent;
            set => SetProperty(ref _irSmoothingPercent, Math.Clamp(value, 0, 100));
        }

        private string _pointerMode = "Mouse";
        /// <summary>Wii pointer mode (issue #203): "Mouse", "FpsMouse",
        /// "Mouse43", "Mouse169". Per (device, slot) on
        /// <see cref="PadSetting.PointerMode"/>. Shapes the cursor drive
        /// only; "IR Pointer X/Y" mapping sources stay raw.</summary>
        public string PointerMode
        {
            get => _pointerMode;
            set => SetProperty(ref _pointerMode, string.IsNullOrEmpty(value) ? "Mouse" : value);
        }

        private int _pointerFpsSpeed = 35;
        /// <summary>FPS Mouse speed (issue #203), the lineage's fpsmouse
        /// speed knob. 35 default, clamped 5..100.</summary>
        public int PointerFpsSpeed
        {
            get => _pointerFpsSpeed;
            set => SetProperty(ref _pointerFpsSpeed, Math.Clamp(value, 5, 100));
        }

        private RelayCommand _resetPointerModeCommand;
        public RelayCommand ResetPointerModeCommand =>
            _resetPointerModeCommand ??= new RelayCommand(() => PointerMode = "Mouse");

        private RelayCommand _resetPointerFpsSpeedCommand;
        public RelayCommand ResetPointerFpsSpeedCommand =>
            _resetPointerFpsSpeedCommand ??= new RelayCommand(() => PointerFpsSpeed = 35);

        private RelayCommand _resetPointerModeCardCommand;
        /// <summary>Card-level Reset All for the Pointer Mode card:
        /// mode + FPS speed together.</summary>
        public RelayCommand ResetPointerModeCardCommand =>
            _resetPointerModeCardCommand ??= new RelayCommand(() =>
            {
                PointerMode = "Mouse";
                PointerFpsSpeed = 35;
            });

        private RelayCommand _resetIrSensorBarPosCommand;
        public RelayCommand ResetIrSensorBarPosCommand =>
            _resetIrSensorBarPosCommand ??= new RelayCommand(() => IrSensorBarPos = 0);

        private RelayCommand _resetIrSensorBarCompCommand;
        public RelayCommand ResetIrSensorBarCompCommand =>
            _resetIrSensorBarCompCommand ??= new RelayCommand(() => IrSensorBarCompPercent = 0);

        private RelayCommand _resetIrSmoothingCommand;
        public RelayCommand ResetIrSmoothingCommand =>
            _resetIrSmoothingCommand ??= new RelayCommand(() => IrSmoothingPercent = 0);

        private RelayCommand _resetPointerTuningCardCommand;
        /// <summary>Card-level Reset All for the Pointer Tuning card: the
        /// #146 tunables all default to zero (IrTunables_DefaultToZero).</summary>
        public RelayCommand ResetPointerTuningCardCommand =>
            _resetPointerTuningCardCommand ??= new RelayCommand(() =>
            {
                IrSensorBarPos = 0;
                IrSensorBarCompPercent = 0;
                IrSmoothingPercent = 0;
            });

        private RelayCommand _resetAudioMirrorCommand;
        /// <summary>Per-row reset for the system-audio mirror toggle. The
        /// Audio card's Reset All already covers it; this is the row
        /// affordance every setting row carries.</summary>
        public RelayCommand ResetAudioMirrorCommand =>
            _resetAudioMirrorCommand ??= new RelayCommand(() =>
            {
                if (DeviceConfig != null) DeviceConfig.AudioPassthroughEnabled = false;
            });

        /// <summary>A render endpoint the mirror can capture; Id "" = the
        /// system default device.</summary>
        public sealed class MirrorSourceOption
        {
            public string Id { get; set; }
            public string Name { get; set; }
        }

        public System.Collections.ObjectModel.ObservableCollection<MirrorSourceOption> MirrorSourceOptions { get; } = new();

        private bool _refreshingMirrorSources;

        /// <summary>Repopulates <see cref="MirrorSourceOptions"/> from the
        /// active render endpoints. Guarded: rebuilding the ItemsSource makes
        /// WPF write a transient null through SelectedValue (the Aim Engage
        /// picker documents the same hazard), so config writes are suppressed
        /// while the list is swapped.</summary>
        public void RefreshMirrorSources()
        {
            _refreshingMirrorSources = true;
            try
            {
                string current = DeviceConfig?.AudioMirrorSourceId ?? string.Empty;
                var desired = new System.Collections.Generic.List<MirrorSourceOption>
                {
                    new MirrorSourceOption { Id = string.Empty, Name = Strings.Instance.Pad_Audio_SystemDefault },
                };
                try
                {
                    using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                    foreach (var dev in en.EnumerateAudioEndPoints(
                        NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
                    {
                        using (dev)
                            desired.Add(new MirrorSourceOption { Id = dev.ID, Name = dev.FriendlyName });
                    }
                }
                catch { }
                if (!string.IsNullOrEmpty(current)
                    && !desired.Any(o => o.Id == current))
                    desired.Add(new MirrorSourceOption { Id = current, Name = Strings.Instance.Pad_Audio_SourceUnavailable });

                // Diff-sync in place — never Clear(). Rebuilding the list
                // removes the ComboBox's selected item instance, and WPF
                // clears the selection even when the rebuilt list carries
                // the same Id; opening and closing the dropdown (which
                // refreshes via DropDownOpened) blanked the field. Items
                // whose Id survives keep their instance, so the selection
                // rides through untouched.
                for (int i = 0; i < desired.Count; i++)
                {
                    int j = -1;
                    for (int k = i; k < MirrorSourceOptions.Count; k++)
                        if (MirrorSourceOptions[k].Id == desired[i].Id) { j = k; break; }
                    if (j < 0) MirrorSourceOptions.Insert(i, desired[i]);
                    else if (j != i) MirrorSourceOptions.Move(j, i);
                }
                while (MirrorSourceOptions.Count > desired.Count)
                    MirrorSourceOptions.RemoveAt(MirrorSourceOptions.Count - 1);

                OnPropertyChanged(nameof(SelectedMirrorSourceId));
            }
            finally { _refreshingMirrorSources = false; }
        }

        /// <summary>ComboBox-facing proxy over
        /// DeviceConfig.AudioMirrorSourceId that ignores the transient
        /// null write-back during ItemsSource rebuilds.</summary>
        public string SelectedMirrorSourceId
        {
            get => DeviceConfig?.AudioMirrorSourceId ?? string.Empty;
            set
            {
                if (_refreshingMirrorSources || value == null) return;
                if (DeviceConfig == null) return;
                if (DeviceConfig.AudioMirrorSourceId == value) return;
                DeviceConfig.AudioMirrorSourceId = value;
                OnPropertyChanged(nameof(SelectedMirrorSourceId));
            }
        }

        private int _soundMasterVolume = 100;
        /// <summary>Per-pad master volume for macro sounds (0-100),
        /// multiplied with each action's own volume. Live sounds retune.</summary>
        public int SoundMasterVolume
        {
            get => _soundMasterVolume;
            set
            {
                int v = Math.Clamp(value, 0, 100);
                if (SetProperty(ref _soundMasterVolume, v))
                {
                    PadForge.Common.Input.SoundMacroService.SetSlotVolume(PadIndex, v);
                    PadForge.Common.Input.UserEffectsDispatcher.NotifySoundRoutingChanged(PadIndex);
                    // Persisted in AppSettings.SlotSoundVolumes; without the
                    // dirty mark a volume-only change skips the close-time
                    // save (OnClosing gates on IsDirty).
                    ConfigItemDirtyCallback?.Invoke();
                }
            }
        }

        /// <summary>This pad's macros that play a sound — the Audio tab's
        /// quick list. Re-derived on Audio-tab entry.</summary>
        public System.Collections.Generic.List<MacroItem> SoundMacros =>
            Macros.Where(m => m.Actions.Any(a => a.Type == MacroActionType.PlaySound)).ToList();

        public bool HasNoSoundMacros => SoundMacros.Count == 0;

        private RelayCommand _soundTestCommand;
        public RelayCommand SoundTestCommand =>
            _soundTestCommand ??= new RelayCommand(
                () =>
                {
                    var dev = SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;
                    // Haptic devices play ONLY the direct fixed-tone (no beep injected,
                    // so the reducer never sees it -> no detection garble, no bleed into
                    // a following macro). Speaker-only devices (no haptic sink, returns
                    // false) demo through the audio beep.
                    if (!PadForge.Common.Input.HapticToneService.TriggerTestTone(dev))
                        PadForge.Common.Input.SoundMacroService.PlayTestBeep(PadIndex, dev);
                },
                () => HasSelectedDevice);

        private RelayCommand _soundStopAllCommand;
        public RelayCommand SoundStopAllCommand =>
            _soundStopAllCommand ??= new RelayCommand(
                () => PadForge.Common.Input.SoundMacroService.StopSlot(PadIndex));

        private RelayCommand _resetSoundMasterVolumeCommand;
        public RelayCommand ResetSoundMasterVolumeCommand =>
            _resetSoundMasterVolumeCommand ??= new RelayCommand(() => SoundMasterVolume = 100);

        private RelayCommand _resetSoundOutputAllCommand;
        /// <summary>Resets the Sound Output card for the selected device:
        /// mirror off, mirror source back to system default, master volume
        /// to 100%, the #185 engage gate back to Always with its defaults,
        /// and the #202 tone filter back to Off / 800 Hz.</summary>
        public RelayCommand ResetSoundOutputAllCommand =>
            _resetSoundOutputAllCommand ??= new RelayCommand(() =>
            {
                SoundMasterVolume = 100;
                if (DeviceConfig != null)
                {
                    DeviceConfig.AudioPassthroughEnabled = false;
                    SelectedMirrorSourceId = string.Empty;
                    DeviceConfig.AudioMirrorEngageMode = "Always";
                    DeviceConfig.AudioMirrorEngageDeviceGuid = string.Empty;
                    DeviceConfig.AudioMirrorEngageButton = string.Empty;
                    DeviceConfig.AudioMirrorEngageReleaseMs = 500;
                    DeviceConfig.AudioToneFilterMode = "Off";
                    DeviceConfig.AudioToneLimitHz = 800;
                    OnPropertyChanged(nameof(MirrorEngageSelectedInput));
                }
            });

        private RelayCommand _addSoundMacroCommand;
        /// <summary>Creates a macro pre-loaded with a Play Sound action and
        /// jumps to the Macros tab to finish it (trigger + file) — the
        /// "best of both worlds" flow: the Audio tab is the hub, the Macros
        /// tab is the editor.</summary>
        public RelayCommand AddSoundMacroCommand =>
            _addSoundMacroCommand ??= new RelayCommand(() =>
            {
                var macro = new MacroItem
                {
                    PadIndex = PadIndex,
                    Name = string.Format(Strings.Instance.Pad_Audio_SoundMacroName_Format, Macros.Count + 1),
                    ButtonStyle = MacroButtonNames.DeriveStyle(_outputType),
                    ExtendedProfileId = SlotExtendedProfileId
                };
                macro.Actions.Add(new MacroAction { Type = MacroActionType.PlaySound });
                Macros.Add(macro);
                SelectedMacro = macro;
                SelectedConfigTab = 1; // Macros tab
            });

        /// <summary>Jump to the Macros tab with the clicked sound macro selected.</summary>
        public void OpenSoundMacro(MacroItem macro)
        {
            if (macro == null) return;
            SelectedMacro = macro;
            SelectedConfigTab = 1;
        }

        /// <summary>The slot's profile slug when it letters Extended button
        /// labels (#215): the ProfileId on Extended slots, null elsewhere.
        /// Xbox / PlayStation lettering keys on ButtonStyle alone.</summary>
        private string SlotExtendedProfileId =>
            _outputType == VirtualControllerType.Extended ? _profileId : null;

        /// <summary>
        /// Syncs macro button display style to all macros when the output
        /// controller type (or an Extended slot's profile, #215) changes.
        /// </summary>
        private void SyncMacroButtonStyle()
        {
            var style = MacroButtonNames.DeriveStyle(_outputType);
            int btnCount = (_outputType == VirtualControllerType.Extended ? _extendedConfig?.ButtonCount : null) ?? 11;
            string letteredProfile = SlotExtendedProfileId;
            foreach (var macro in Macros)
            {
                macro.ButtonStyle = style;
                macro.CustomButtonCount = btnCount;
                macro.ExtendedProfileId = letteredProfile;
                foreach (var action in macro.Actions)
                    action.CustomButtonCount = btnCount;
            }
            // The menu editor's cell pickers follow the same lettering.
            foreach (var menu in Menus)
                ApplyMenuButtonStyle(menu);
        }

        /// <summary>Stamps the slot's context onto a menu editor item: the
        /// same button-lettering derivation and Extended button count the
        /// macro editor uses (see <see cref="SyncMacroButtonStyle"/>).
        /// The opener picker itself is device-agnostic (the mapping
        /// table's "(Any device)" convention) and needs no slot caps.</summary>
        private void ApplyMenuButtonStyle(MenuEditorItem vm)
        {
            vm.ButtonStyle = MacroButtonNames.DeriveStyle(_outputType);
            vm.ExtendedButtonCount =
                (_outputType == VirtualControllerType.Extended ? _extendedConfig?.ButtonCount : null) ?? 11;
            vm.ExtendedProfileId = SlotExtendedProfileId;
            // MIDI / Keyboard-Mouse outputs cannot press controller
            // buttons: their cells' binding-kind list omits the choice
            // entirely instead of offering it plus a warning.
            vm.SupportsControllerButtons = _outputType
                is VirtualControllerType.Xbox
                or VirtualControllerType.PlayStation
                or VirtualControllerType.Extended;
            vm.DescriptorDisplayProvider = ResolveInputDisplayName;
            vm.InputChoicesProvider = () => SlotAvailableInputs;
            vm.RowBoundProvider = IsMenuItemRowBound;
            vm.StructureChanged = () => MenusStructureChanged?.Invoke();
        }

        /// <summary>True when a Mappings row source or a macro trigger on
        /// this slot reads "Menu {menuId} Item {itemIndex}". Workshop
        /// imports bind cells through rows instead of the cell's direct
        /// binding, and the cell editor must not show "None" while those
        /// rows fire (audit 2026-07-16 C12). Same unlocked UI-thread read
        /// of the slot's MappingSet as SlotMenuSet.</summary>
        internal bool IsMenuItemRowBound(int menuId, int itemIndex)
        {
            var rows = SlotMenuSet?.Rows;
            if (rows != null)
            {
                foreach (var row in rows)
                {
                    var sources = row?.Sources;
                    if (sources == null) continue;
                    foreach (var src in sources)
                        if (src != null
                            && PadForge.Engine.Common.Mapping.SourceCoercion.TryParseMenuItem(
                                src.Descriptor, out int m, out int k)
                            && m == menuId && k == itemIndex)
                            return true;
                }
            }
            foreach (var macro in Macros)
            {
                if (macro == null) continue;
                foreach (var entry in macro.GetTriggerInputEntries())
                    if (entry != null
                        && PadForge.Engine.Common.Mapping.SourceCoercion.TryParseMenuItem(
                            entry.SourceDescriptor, out int m, out int k)
                        && m == menuId && k == itemIndex)
                        return true;
            }
            return false;
        }

        /// <summary>Re-raises every menu editor's Custom steer / Click
        /// dropdowns after the slot's picker list repopulates. Called by
        /// PopulateAvailableInputs beside the other per-feature selected-
        /// input refreshers.</summary>
        public void RefreshMenuInputChoices()
        {
            foreach (var menu in Menus)
                menu.RefreshInputChoices();
        }

        /// <summary>Friendly name for a recorded raw descriptor: the
        /// slot's picker choice when one matches, the raw descriptor
        /// otherwise (a device family the picker does not list).</summary>
        private string ResolveInputDisplayName(string descriptor)
        {
            if (string.IsNullOrEmpty(descriptor)) return descriptor;
            foreach (var c in SlotAvailableInputs)
                if (c != null && string.Equals(c.Descriptor, descriptor, StringComparison.Ordinal))
                    return c.DisplayName;
            return descriptor;
        }

        // ═══════════════════════════════════════════════
        //  Bass Shakers tab (issue #236, rumble to audio)
        // ═══════════════════════════════════════════════

        // The config lives on the slot's MappingSet (RumbleAudio), the
        // same lifetime as the mapping rows, macros, and menus. Every
        // setter writes the DTO in place, marks the settings dirty (the
        // Menus tab's OnMenuEdited shape), and nudges the renderer off
        // the UI thread so edits apply while the tone plays.

        /// <summary>Tab-strip index of the Bass Shakers tab (#236).</summary>
        public const int BassShakersTabIndex = 16;

        /// <summary>Slot-type gate for the Bass Shakers tab. The feature
        /// decodes the game feedback the virtual controller RECEIVES,
        /// so every output path with a feedback surface shows it: Xbox
        /// and Sony motor lanes plus the PID force feedback lane on
        /// Extended slots (the sim-racing wheels that filed #234).
        /// Extended without force feedback, Keyboard and Mouse, and
        /// MIDI have no feedback surface and hide the tab. Never a
        /// physical-device capability gate.</summary>
        public bool RumbleAudioTabVisible
        {
            get
            {
                if (_outputType is VirtualControllerType.Xbox
                    or VirtualControllerType.PlayStation)
                    return true;
                if (_outputType != VirtualControllerType.Extended)
                    return false;
                // Mirrors the wire truth Step 5 builds: Customize on means
                // the checkbox decides in both directions (the descriptor
                // is rebuilt to match). Customize off means the catalog
                // profile's own descriptor decides, and most catalog
                // profiles ship without a PID block.
                var cfg = ExtendedConfig;
                if (cfg != null && cfg.Customize)
                    return cfg.ForceFeedbackEnabled;
                var profile = PadForge.Common.Input.HMaestroProfileCatalog
                    .GetProfileById(ProfileId);
                return PadForge.Common.Input.HMaestroVirtualController
                    .DescriptorHasPidFfbBlock(profile?.DescriptorHex);
            }
        }

        /// <summary>Re-evaluates the Bass Shakers tab gate and evicts a
        /// now-hidden selection back to the first tab. Call from every
        /// seam that changes the gate's inputs: the force feedback
        /// checkbox, the Customize toggle, and an uncustomized profile
        /// switch.</summary>
        private void RefreshRumbleAudioTabGate()
        {
            OnPropertyChanged(nameof(RumbleAudioTabVisible));
            if (!RumbleAudioTabVisible
                && SelectedConfigTab == BassShakersTabIndex)
                SelectedConfigTab = 0;
        }

        /// <summary>The slot's authored config, null until first enable.
        /// Read-only here. Creation happens in the enable setter only.</summary>
        private RumbleAudioConfig RumbleAudioCfg => SlotMenuSet?.RumbleAudio;

        /// <summary>The four fixed voice rows (low, high, trigger left,
        /// trigger right), built once in the constructor.</summary>
        public ObservableCollection<RumbleAudioVoiceItem> RumbleAudioVoices { get; } = new();

        /// <summary>Master enable. The first enable authors the config
        /// with the four default voices. Disable keeps every authored
        /// setting and only silences the renderer.</summary>
        public bool RumbleAudioEnabled
        {
            get => RumbleAudioCfg?.Enabled ?? false;
            set
            {
                var set = SlotMenuSet;
                if (set == null) return;
                var cfg = set.RumbleAudio;
                if (cfg == null)
                {
                    if (!value) return;
                    cfg = new RumbleAudioConfig();
                    for (int i = 0; i < RumbleAudioConfig.SourceOrder.Length; i++)
                        cfg.Voices.Add(new RumbleAudioVoice
                        {
                            Source = RumbleAudioConfig.SourceOrder[i],
                            Enabled = true,
                            GainPercent = 100,
                            FrequencyHz = RumbleAudioConfig.DefaultFrequencyHz[i],
                        });
                    set.RumbleAudio = cfg;
                }
                if (cfg.Enabled == value) return;
                cfg.Enabled = value;
                OnPropertyChanged(nameof(RumbleAudioEnabled));
                NotifyRumbleAudioConfigChanged();
            }
        }

        private bool _refreshingRumbleAudioEndpoints;

        /// <summary>ComboBox-facing endpoint id. Empty targets the system
        /// default render endpoint. Guarded against the transient null
        /// write-back during ItemsSource rebuilds (the mirror-source
        /// picker documents the same hazard).</summary>
        public string RumbleAudioEndpointId
        {
            get => RumbleAudioCfg?.EndpointId ?? string.Empty;
            set
            {
                if (_refreshingRumbleAudioEndpoints || value == null) return;
                var cfg = RumbleAudioCfg;
                if (cfg == null || cfg.EndpointId == value) return;
                cfg.EndpointId = value;
                OnPropertyChanged(nameof(RumbleAudioEndpointId));
                NotifyRumbleAudioConfigChanged();
            }
        }

        /// <summary>Speaker placement. Empty = mono (every voice on all
        /// channels), "Stereo" = controller stereo (low and left trigger
        /// left, high and right trigger right).</summary>
        public string RumbleAudioChannelMode
        {
            get => RumbleAudioCfg?.ChannelMode ?? string.Empty;
            set
            {
                if (value == null) return;
                var cfg = RumbleAudioCfg;
                if (cfg == null || cfg.ChannelMode == value) return;
                cfg.ChannelMode = value;
                OnPropertyChanged(nameof(RumbleAudioChannelMode));
                NotifyRumbleAudioConfigChanged();
            }
        }

        /// <summary>Master gain percent, 0..100, applied after per-voice
        /// gain. Default 50 keeps headroom for the four-voice sum.</summary>
        public int RumbleAudioMasterGain
        {
            get => RumbleAudioCfg?.MasterGainPercent ?? 50;
            set
            {
                var cfg = RumbleAudioCfg;
                if (cfg == null) return;
                int v = Math.Clamp(value, 0, 100);
                if (cfg.MasterGainPercent == v) return;
                cfg.MasterGainPercent = v;
                OnPropertyChanged(nameof(RumbleAudioMasterGain));
                NotifyRumbleAudioConfigChanged();
            }
        }

        /// <summary>Active render endpoints for the output picker. Reuses
        /// the mirror-source option shape (Id "" = system default).</summary>
        public System.Collections.ObjectModel.ObservableCollection<MirrorSourceOption> RumbleAudioEndpointOptions { get; } = new();

        /// <summary>Repopulates <see cref="RumbleAudioEndpointOptions"/>
        /// from the active render endpoints. Diff-syncs in place so the
        /// ComboBox selection rides through a refresh (never Clear()).
        /// A configured endpoint that is currently absent stays listed as
        /// unavailable so the fail-closed selection remains visible.</summary>
        public void RefreshRumbleAudioEndpoints()
        {
            _refreshingRumbleAudioEndpoints = true;
            try
            {
                string current = RumbleAudioEndpointId;
                var desired = new System.Collections.Generic.List<MirrorSourceOption>
                {
                    new MirrorSourceOption { Id = string.Empty, Name = Strings.Instance.Pad_Audio_SystemDefault },
                };
                try
                {
                    using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                    foreach (var dev in en.EnumerateAudioEndPoints(
                        NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
                    {
                        using (dev)
                            desired.Add(new MirrorSourceOption { Id = dev.ID, Name = dev.FriendlyName });
                    }
                }
                catch { }
                if (!string.IsNullOrEmpty(current)
                    && !desired.Any(o => o.Id == current))
                    desired.Add(new MirrorSourceOption { Id = current, Name = Strings.Instance.Pad_Audio_SourceUnavailable });

                for (int i = 0; i < desired.Count; i++)
                {
                    int j = -1;
                    for (int k = i; k < RumbleAudioEndpointOptions.Count; k++)
                        if (RumbleAudioEndpointOptions[k].Id == desired[i].Id) { j = k; break; }
                    if (j < 0) RumbleAudioEndpointOptions.Insert(i, desired[i]);
                    else if (j != i) RumbleAudioEndpointOptions.Move(j, i);
                }
                while (RumbleAudioEndpointOptions.Count > desired.Count)
                    RumbleAudioEndpointOptions.RemoveAt(RumbleAudioEndpointOptions.Count - 1);

                OnPropertyChanged(nameof(RumbleAudioEndpointId));
            }
            finally { _refreshingRumbleAudioEndpoints = false; }
        }

        /// <summary>Rebuilds the Bass Shakers tab state from the slot's
        /// live MappingSet. Called beside <see cref="ReloadMenus"/> from
        /// every path that swaps or rewrites the slot set (profile apply,
        /// Workshop import, output-type change, Reset to Defaults, paste,
        /// Copy From Slot, slot delete).</summary>
        public void ReloadRumbleAudio()
        {
            var cfg = RumbleAudioCfg;
            for (int i = 0; i < RumbleAudioVoices.Count; i++)
                RumbleAudioVoices[i].LoadFrom(cfg?.FindVoice(RumbleAudioConfig.SourceOrder[i]));
            OnPropertyChanged(nameof(RumbleAudioEnabled));
            OnPropertyChanged(nameof(RumbleAudioEndpointId));
            OnPropertyChanged(nameof(RumbleAudioChannelMode));
            OnPropertyChanged(nameof(RumbleAudioMasterGain));
            OnPropertyChanged(nameof(RumbleAudioTabVisible));
        }

        /// <summary>Writes one voice row's edits into the DTO voice,
        /// authoring the voice entry when the config predates it.</summary>
        internal void WriteRumbleAudioVoice(int index, bool enabled, int gainPercent, int frequencyHz)
        {
            var cfg = RumbleAudioCfg;
            if (cfg == null || (uint)index >= RumbleAudioConfig.SourceOrder.Length) return;
            string source = RumbleAudioConfig.SourceOrder[index];
            var voice = cfg.FindVoice(source);
            if (voice == null)
            {
                voice = new RumbleAudioVoice { Source = source };
                cfg.Voices.Add(voice);
            }
            voice.Enabled = enabled;
            voice.GainPercent = Math.Clamp(gainPercent, 0, 100);
            voice.FrequencyHz = Math.Clamp(frequencyHz,
                RumbleAudioConfig.MinFrequencyHz, RumbleAudioConfig.MaxFrequencyHz);
            NotifyRumbleAudioConfigChanged();
        }

        /// <summary>Every persisted edit funnels here. Marks the settings
        /// dirty (without it the change never reaches disk) and reconciles
        /// the renderer on a worker task. Reconcile touches WASAPI, so it
        /// must never run on the UI thread's critical path.</summary>
        // Debounced reconcile kick (audit: a gain-slider drag fired one
        // Task.Run + full COM endpoint enumeration per notch). One shared
        // 300 ms one-shot timer per VM: every change re-arms it, the
        // trailing edge runs a single reconcile with the final values.
        private System.Threading.Timer _rumbleAudioReconcileDebounce;

        private void NotifyRumbleAudioConfigChanged()
        {
            ConfigItemDirtyCallback?.Invoke();
            // Reconcile ONLY, never EnsureStarted: a debounce firing after
            // the engine stopped must not resurrect the reconcile worker.
            // ReconcileCore's commit gate rejects new players while the
            // worker timer is null, so a post-stop fire builds nothing
            // lasting, and the engine-start EnsureStarted re-arms the
            // worker for the running case.
            var timer = _rumbleAudioReconcileDebounce ??= new System.Threading.Timer(_ =>
            {
                try { RumbleAudioService.Reconcile(); }
                catch { }
            }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            timer.Change(300, System.Threading.Timeout.Infinite);
        }

        private RelayCommand _resetRumbleAudioEnabledCommand;
        /// <summary>Per-row reset for the enable toggle. Default is off.</summary>
        public RelayCommand ResetRumbleAudioEnabledCommand =>
            _resetRumbleAudioEnabledCommand ??= new RelayCommand(() => RumbleAudioEnabled = false);

        private RelayCommand _resetRumbleAudioEndpointCommand;
        public RelayCommand ResetRumbleAudioEndpointCommand =>
            _resetRumbleAudioEndpointCommand ??= new RelayCommand(() => RumbleAudioEndpointId = string.Empty);

        private RelayCommand _resetRumbleAudioChannelModeCommand;
        public RelayCommand ResetRumbleAudioChannelModeCommand =>
            _resetRumbleAudioChannelModeCommand ??= new RelayCommand(() => RumbleAudioChannelMode = string.Empty);

        private RelayCommand _resetRumbleAudioMasterGainCommand;
        public RelayCommand ResetRumbleAudioMasterGainCommand =>
            _resetRumbleAudioMasterGainCommand ??= new RelayCommand(() => RumbleAudioMasterGain = 50);

        private RelayCommand _resetRumbleAudioAllCommand;
        /// <summary>Card-level Reset All: endpoint to system default, mono,
        /// master gain 50, every voice to enabled / 100 / default carrier.
        /// Keeps Enabled as it is (the enable row's own reset covers it).</summary>
        public RelayCommand ResetRumbleAudioAllCommand =>
            _resetRumbleAudioAllCommand ??= new RelayCommand(() =>
            {
                if (RumbleAudioCfg == null) return;
                RumbleAudioEndpointId = string.Empty;
                RumbleAudioChannelMode = string.Empty;
                RumbleAudioMasterGain = 50;
                foreach (var voice in RumbleAudioVoices)
                    voice.ResetToDefault();
            });

        private RelayCommand _rumbleAudioSweepCommand;
        /// <summary>Resonance finder: sweeps 20..120 Hz over eight seconds
        /// on the low-voice routing.</summary>
        public RelayCommand RumbleAudioSweepCommand =>
            _rumbleAudioSweepCommand ??= new RelayCommand(() =>
                RumbleAudioService.StartSweep(PadIndex, 8000));

        private RelayCommand _rumbleAudioStopTestCommand;
        public RelayCommand RumbleAudioStopTestCommand =>
            _rumbleAudioStopTestCommand ??= new RelayCommand(() =>
                RumbleAudioService.StopTest(PadIndex));

        // ═══════════════════════════════════════════════
        //  Simultaneous Press (SOCD), discussion #240
        //  Authoring UI over MappingSet.SocdMode / SocdPairs, the
        //  slot-scoped config Step 5 already applies to the FINAL combined
        //  output (SlotButtonSocd). Same MappingSet lane as Menus and
        //  RumbleAudio: reads and writes go through SlotMenuSet, every
        //  persisted edit marks dirty, and ReloadSocd re-seeds the rows on
        //  the same paths that reload those features.
        // ═══════════════════════════════════════════════

        /// <summary>Slot-type gate for the SOCD card. It shapes the slot's
        /// virtual controller BUTTON output, so KeyboardMouse (its own
        /// Snap Tap bar) and MIDI (no button surface) hide it.</summary>
        public bool SocdCardVisible =>
            _outputType is VirtualControllerType.Xbox
            or VirtualControllerType.PlayStation
            or VirtualControllerType.Extended;

        /// <summary>True when the slot's pairs use the flat raw-index
        /// grammar ("12:13"). Mirrors the engine gate exactly: Step 5
        /// treats every Extended slot as raw-surface
        /// (SlotExtendedIsCustom == OutputType is Extended), so Extended
        /// pairs are indices and Xbox / PlayStation pairs are the
        /// WriteBoolTarget names.</summary>
        public bool SocdUsesExtendedIndices =>
            _outputType == VirtualControllerType.Extended;

        /// <summary>SOCD mode, locale-stable: "" (off), "LastWins",
        /// "Neutral", "FirstWins". Stored on the slot's MappingSet so it
        /// rides profiles, Copy From Slot, and the export container.</summary>
        public string SocdMode
        {
            get => SlotMenuSet?.SocdMode ?? "";
            set
            {
                if (value == null) return;
                var set = SlotMenuSet;
                if (set == null || string.Equals(set.SocdMode, value, StringComparison.Ordinal)) return;
                set.SocdMode = value;
                OnPropertyChanged(nameof(SocdMode));
                ConfigItemDirtyCallback?.Invoke();
            }
        }

        /// <summary>Mode dropdown options. Names are shared with the KBM
        /// Snap Tap bar (same state machine); descriptions are worded for
        /// buttons. Built fresh per read (tiny list) so a culture change
        /// re-letters through the ReloadSocd raise.</summary>
        public System.Collections.Generic.IReadOnlyList<SocdModeOption> AvailableSlotSocdModes
        {
            get
            {
                var s = Strings.Instance;
                return new[]
                {
                    new SocdModeOption { Value = "",          Name = s.Pad_Kbm_Socd_Mode_Off_Name,       Description = s.Pad_Socd_Mode_Off_Description },
                    new SocdModeOption { Value = "LastWins",  Name = s.Pad_Kbm_Socd_Mode_LastWins_Name,  Description = s.Pad_Socd_Mode_LastWins_Description },
                    new SocdModeOption { Value = "Neutral",   Name = s.Pad_Kbm_Socd_Mode_Neutral_Name,   Description = s.Pad_Socd_Mode_Neutral_Description },
                    new SocdModeOption { Value = "FirstWins", Name = s.Pad_Kbm_Socd_Mode_FirstWins_Name, Description = s.Pad_Socd_Mode_FirstWins_Description },
                };
            }
        }

        /// <summary>Pickable pair targets for Xbox / PlayStation slots: the
        /// 15 WriteBoolTarget names in the mapping grid's own lettering
        /// (PlayStation symbols on PlayStation slots). Values are the
        /// locale-stable target-name grammar SlotButtonSocd parses.</summary>
        public System.Collections.Generic.IReadOnlyList<GyroLabeledOption> SocdButtonOptions
        {
            get
            {
                var s = Strings.Instance;
                bool ps = _outputType == VirtualControllerType.PlayStation;
                return new[]
                {
                    new GyroLabeledOption(() => ps ? "✕" : "A", "ButtonA"),
                    new GyroLabeledOption(() => ps ? "○" : "B", "ButtonB"),
                    new GyroLabeledOption(() => ps ? "◻" : "X", "ButtonX"),
                    new GyroLabeledOption(() => ps ? "△" : "Y", "ButtonY"),
                    new GyroLabeledOption(() => ps ? "L1" : s.Btn_LeftShoulder, "LeftShoulder"),
                    new GyroLabeledOption(() => ps ? "R1" : s.Btn_RightShoulder, "RightShoulder"),
                    new GyroLabeledOption(() => ps ? s.Btn_Share : s.Btn_Back, "ButtonBack"),
                    new GyroLabeledOption(() => ps ? s.Btn_Options : s.Btn_Start, "ButtonStart"),
                    new GyroLabeledOption(() => ps ? s.Btn_PS : s.Btn_Guide, "ButtonGuide"),
                    new GyroLabeledOption(() => ps ? "L3" : s.Btn_LeftStickButton, "LeftThumbButton"),
                    new GyroLabeledOption(() => ps ? "R3" : s.Btn_RightStickButton, "RightThumbButton"),
                    new GyroLabeledOption(() => s.Btn_DPadUp, "DPadUp"),
                    new GyroLabeledOption(() => s.Btn_DPadDown, "DPadDown"),
                    new GyroLabeledOption(() => s.Btn_DPadLeft, "DPadLeft"),
                    new GyroLabeledOption(() => s.Btn_DPadRight, "DPadRight"),
                };
            }
        }

        /// <summary>Editable projection of MappingSet.SocdPairs for the
        /// card's pair rows.</summary>
        public ObservableCollection<SlotSocdPairItem> SocdPairItems { get; } = new();

        private bool _syncingSocdPairs;

        /// <summary>Reserializes the rows into the pipe grammar. Called by
        /// row setters and the add / remove / reset commands.</summary>
        // Pair tokens the editor cannot display (the other slot type's
        // grammar after an output-type switch, or unresolvable names).
        // Preserved verbatim so an edit never erases them: the stored
        // field's contract is "malformed entries are dropped at parse,
        // never at save" (MappingSet.SocdPairs), and the engine already
        // ignores them harmlessly at runtime.
        private readonly System.Collections.Generic.List<string> _socdPreservedTokens = new();

        internal void OnSocdPairEdited()
        {
            if (_syncingSocdPairs) return;
            var set = SlotMenuSet;
            if (set == null) return;
            var sb = new System.Text.StringBuilder();
            foreach (var p in SocdPairItems)
            {
                string token = p.Serialize();
                if (token == null) continue;
                if (sb.Length > 0) sb.Append('|');
                sb.Append(token);
            }
            foreach (var kept in _socdPreservedTokens)
            {
                if (sb.Length > 0) sb.Append('|');
                sb.Append(kept);
            }
            string pairs = sb.ToString();
            if (string.Equals(set.SocdPairs, pairs, StringComparison.Ordinal)) return;
            set.SocdPairs = pairs;
            ConfigItemDirtyCallback?.Invoke();
        }

        /// <summary>Re-seeds the SOCD card from the slot's MappingSet.
        /// Called on the same paths that reload Menus and RumbleAudio
        /// (RebuildMappings, slot delete, Paste, Copy From Slot).</summary>
        public void ReloadSocd()
        {
            _syncingSocdPairs = true;
            try
            {
                SocdPairItems.Clear();
                _socdPreservedTokens.Clear();
                var set = SlotMenuSet;
                bool extended = SocdUsesExtendedIndices;
                if (set != null && !string.IsNullOrEmpty(set.SocdPairs))
                {
                    foreach (var token in set.SocdPairs.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    {
                        int colon = token.IndexOf(':');
                        if (colon <= 0 || colon >= token.Length - 1) { _socdPreservedTokens.Add(token); continue; }
                        string a = token.Substring(0, colon).Trim();
                        string b = token.Substring(colon + 1).Trim();
                        if (extended)
                        {
                            if (!int.TryParse(a, out int ia) || !int.TryParse(b, out int ib)
                                || ia is < 0 or > 127 || ib is < 0 or > 127)
                            { _socdPreservedTokens.Add(token); continue; }
                            SocdPairItems.Add(new SlotSocdPairItem(this, ia, ib));
                        }
                        else
                        {
                            // Display only names the engine resolves, the
                            // same leniency SlotButtonSocd.ParsePairs
                            // applies at runtime, but PRESERVE the token:
                            // it may be the other slot type's grammar and
                            // must survive a round trip through this
                            // editor (the stored field's own contract).
                            if (PadForge.Common.Input.SlotButtonSocd.ResolveGamepadMask(a) == 0
                                || PadForge.Common.Input.SlotButtonSocd.ResolveGamepadMask(b) == 0)
                            { _socdPreservedTokens.Add(token); continue; }
                            SocdPairItems.Add(new SlotSocdPairItem(this, a, b));
                        }
                    }
                }
            }
            finally
            {
                _syncingSocdPairs = false;
            }
            OnPropertyChanged(nameof(SocdMode));
            OnPropertyChanged(nameof(SocdCardVisible));
            OnPropertyChanged(nameof(SocdUsesExtendedIndices));
            OnPropertyChanged(nameof(AvailableSlotSocdModes));
            OnPropertyChanged(nameof(SocdButtonOptions));
        }

        private RelayCommand _addSocdPairCommand;
        /// <summary>Appends a fresh pair for the user to retarget: the
        /// D-Pad Left / Right opposites on gamepad slots, raw indices
        /// 0 and 1 on Extended slots.</summary>
        public RelayCommand AddSocdPairCommand =>
            _addSocdPairCommand ??= new RelayCommand(() =>
            {
                SocdPairItems.Add(SocdUsesExtendedIndices
                    ? new SlotSocdPairItem(this, 0, 1)
                    : new SlotSocdPairItem(this, "DPadLeft", "DPadRight"));
                OnSocdPairEdited();
            });

        private RelayCommand<SlotSocdPairItem> _removeSocdPairCommand;
        public RelayCommand<SlotSocdPairItem> RemoveSocdPairCommand =>
            _removeSocdPairCommand ??= new RelayCommand<SlotSocdPairItem>(item =>
            {
                if (item == null) return;
                SocdPairItems.Remove(item);
                OnSocdPairEdited();
            });

        private RelayCommand _resetSocdModeCommand;
        /// <summary>Per-row reset for the mode combo. Default is off.</summary>
        public RelayCommand ResetSocdModeCommand =>
            _resetSocdModeCommand ??= new RelayCommand(() => SocdMode = "");

        private RelayCommand _resetSocdCardCommand;
        /// <summary>Card-level Reset All: mode off, every pair removed.</summary>
        public RelayCommand ResetSocdCardCommand =>
            _resetSocdCardCommand ??= new RelayCommand(() =>
            {
                SocdMode = "";
                if (SocdPairItems.Count > 0)
                {
                    SocdPairItems.Clear();
                    OnSocdPairEdited();
                }
            });

        /// <summary>One editable SOCD pair row (#240). Gamepad rows carry
        /// two target NAMES for the pickers; Extended rows carry two raw
        /// button INDICES for the numeric boxes. Edits reserialize the
        /// owning slot's MappingSet.SocdPairs.</summary>
        public sealed class SlotSocdPairItem : ObservableObject
        {
            private readonly PadViewModel _owner;

            /// <summary>Gamepad-name row.</summary>
            internal SlotSocdPairItem(PadViewModel owner, string targetA, string targetB)
            {
                _owner = owner;
                IsExtended = false;
                _targetA = targetA;
                _targetB = targetB;
            }

            /// <summary>Extended raw-index row.</summary>
            internal SlotSocdPairItem(PadViewModel owner, int indexA, int indexB)
            {
                _owner = owner;
                IsExtended = true;
                _indexA = indexA;
                _indexB = indexB;
            }

            /// <summary>True when this row edits flat raw indices (the
            /// Extended grammar) instead of target names.</summary>
            public bool IsExtended { get; }

            private string _targetA;
            public string TargetA
            {
                get => _targetA;
                set
                {
                    // Ignore the transient null write-back a ComboBox emits
                    // while its ItemsSource rebuilds. Removal is the row's
                    // only sanctioned clear path.
                    if (value == null) return;
                    if (SetProperty(ref _targetA, value))
                        _owner?.OnSocdPairEdited();
                }
            }

            private string _targetB;
            public string TargetB
            {
                get => _targetB;
                set
                {
                    if (value == null) return;
                    if (SetProperty(ref _targetB, value))
                        _owner?.OnSocdPairEdited();
                }
            }

            private int _indexA;
            public int IndexA
            {
                get => _indexA;
                set
                {
                    if (SetProperty(ref _indexA, Math.Clamp(value, 0, 127)))
                        _owner?.OnSocdPairEdited();
                }
            }

            private int _indexB;
            public int IndexB
            {
                get => _indexB;
                set
                {
                    if (SetProperty(ref _indexB, Math.Clamp(value, 0, 127)))
                        _owner?.OnSocdPairEdited();
                }
            }

            /// <summary>Items source for both pickers on gamepad rows.</summary>
            public System.Collections.Generic.IReadOnlyList<GyroLabeledOption> ButtonOptions
                => _owner?.SocdButtonOptions;

            /// <summary>The row's "A:B" token, or null when the row is not
            /// yet a valid pair (same-target or unresolved halves are kept
            /// on screen for the user to finish, and dropped from the
            /// persisted grammar exactly as the engine parser would drop
            /// them).</summary>
            internal string Serialize()
            {
                if (IsExtended)
                {
                    if (_indexA == _indexB) return null;
                    return _indexA.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ":" + _indexB.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                if (string.IsNullOrEmpty(_targetA) || string.IsNullOrEmpty(_targetB)) return null;
                if (string.Equals(_targetA, _targetB, StringComparison.Ordinal)) return null;
                return _targetA + ":" + _targetB;
            }
        }

        /// <summary>One Bass Shakers voice row (#236). Keyed by voice
        /// index into <see cref="RumbleAudioConfig.SourceOrder"/>. Edits
        /// route through the owner so the row never holds a reference to
        /// a DTO the profile-apply path may have replaced.</summary>
        public sealed class RumbleAudioVoiceItem : ObservableObject
        {
            private readonly PadViewModel _owner;
            private readonly int _index;
            private bool _loading;
            private bool _enabled = true;
            private int _gainPercent = 100;
            private int _frequencyHz;
            private double _meterLevel;

            internal RumbleAudioVoiceItem(PadViewModel owner, int index)
            {
                _owner = owner;
                _index = index;
                _frequencyHz = RumbleAudioConfig.DefaultFrequencyHz[index];
            }

            /// <summary>Localized row label, live lookup so a language
            /// switch re-renders without rebuilding the list.</summary>
            public string Label => _index switch
            {
                0 => Strings.Instance.Pad_RumbleAudio_Voice_Low,
                1 => Strings.Instance.Pad_RumbleAudio_Voice_High,
                2 => Strings.Instance.Pad_RumbleAudio_Voice_TriggerLeft,
                _ => Strings.Instance.Pad_RumbleAudio_Voice_TriggerRight,
            };

            /// <summary>Per-row reset tooltip, "Reset {Row}" per canon.</summary>
            public string ResetTooltip => _index switch
            {
                0 => Strings.Instance.Pad_RumbleAudio_ResetVoiceLow,
                1 => Strings.Instance.Pad_RumbleAudio_ResetVoiceHigh,
                2 => Strings.Instance.Pad_RumbleAudio_ResetVoiceTriggerLeft,
                _ => Strings.Instance.Pad_RumbleAudio_ResetVoiceTriggerRight,
            };

            /// <summary>Per-voice enable. Gain and frequency stay authored
            /// while muted.</summary>
            public bool Enabled
            {
                get => _enabled;
                set { if (SetProperty(ref _enabled, value)) Push(); }
            }

            /// <summary>Voice gain percent, 0..100, before master gain.</summary>
            public int GainPercent
            {
                get => _gainPercent;
                set
                {
                    int v = Math.Clamp(value, 0, 100);
                    if (SetProperty(ref _gainPercent, v)) Push();
                }
            }

            /// <summary>Sine carrier frequency in Hz, 20..120.</summary>
            public int FrequencyHz
            {
                get => _frequencyHz;
                set
                {
                    int v = Math.Clamp(value,
                        RumbleAudioConfig.MinFrequencyHz, RumbleAudioConfig.MaxFrequencyHz);
                    if (SetProperty(ref _frequencyHz, v)) Push();
                }
            }

            /// <summary>Live activity 0..100 for the row meter, written by
            /// the page's meter timer. Never persisted.</summary>
            public double MeterLevel
            {
                get => _meterLevel;
                set => SetProperty(ref _meterLevel, value);
            }

            private RelayCommand _testCommand;
            /// <summary>Plays this voice for 1.5 seconds at authored gain.</summary>
            public RelayCommand TestCommand =>
                _testCommand ??= new RelayCommand(() =>
                    RumbleAudioService.PulseTestVoice(_owner.PadIndex, _index, 1500));

            private RelayCommand _resetCommand;
            /// <summary>Row reset: enabled, gain 100, default carrier.</summary>
            public RelayCommand ResetCommand =>
                _resetCommand ??= new RelayCommand(ResetToDefault);

            internal void ResetToDefault()
            {
                Enabled = true;
                GainPercent = 100;
                FrequencyHz = RumbleAudioConfig.DefaultFrequencyHz[_index];
            }

            /// <summary>Re-seeds the row from the DTO voice (null reads as
            /// the source's default voice) without writing back.</summary>
            internal void LoadFrom(RumbleAudioVoice voice)
            {
                _loading = true;
                try
                {
                    Enabled = voice?.Enabled ?? true;
                    GainPercent = voice?.GainPercent ?? 100;
                    FrequencyHz = voice?.FrequencyHz ?? RumbleAudioConfig.DefaultFrequencyHz[_index];
                }
                finally { _loading = false; }
                OnPropertyChanged(nameof(Label));
                OnPropertyChanged(nameof(ResetTooltip));
            }

            private void Push()
            {
                if (_loading) return;
                _owner.WriteRumbleAudioVoice(_index, _enabled, _gainPercent, _frequencyHz);
            }
        }

        // ═══════════════════════════════════════════════
        //  Active config tab
        // ═══════════════════════════════════════════════

        private int _selectedConfigTab;

        /// <summary>
        /// 0=Controller, 1=Macros, 2=Mappings, 3=Sticks, 4=Triggers, 5=Force Feedback
        /// </summary>
        public int SelectedConfigTab
        {
            get => _selectedConfigTab;
            set
            {
                if (!SetProperty(ref _selectedConfigTab, value)) return;
                if (value == AudioTabIndex)
                {
                    // Entering the Audio tab: re-derive the sound-macro list,
                    // the selected device's speaker capability, and the
                    // mirror-source endpoint list.
                    OnPropertyChanged(nameof(SoundMacros));
                    OnPropertyChanged(nameof(HasNoSoundMacros));
                    OnPropertyChanged(nameof(SelectedDeviceHasSpeaker));
                    OnPropertyChanged(nameof(SelectedDeviceHasNoSpeaker));
                    OnPropertyChanged(nameof(SelectedDeviceHasHapticTones));
                    OnPropertyChanged(nameof(MirrorEngageSelectedInput));
                    RefreshMirrorSources();
                }
                else if (value == BassShakersTabIndex)
                {
                    // Entering the Bass Shakers tab: re-enumerate render
                    // endpoints so hot-plugged devices show up, and re-seed
                    // the rows from the slot set.
                    RefreshRumbleAudioEndpoints();
                    ReloadRumbleAudio();
                }
            }
        }

        /// <summary>Tab-strip index of the Audio tab (issue #83).</summary>
        public const int AudioTabIndex = 12;

        private bool _annotationOverlayEnabled;

        /// <summary>Controller-preview annotation overlay, shared by the 2D and
        /// 3D hosts (#175 roadmap 1). Session-only by design: not written to
        /// PadSetting, not in the MarkDirty allowlist.</summary>
        public bool AnnotationOverlayEnabled
        {
            get => _annotationOverlayEnabled;
            set => SetProperty(ref _annotationOverlayEnabled, value);
        }

        // ═══════════════════════════════════════════════
        //  Commands
        // ═══════════════════════════════════════════════

        private RelayCommand _testRumbleCommand;
        public RelayCommand TestRumbleCommand =>
            _testRumbleCommand ??= new RelayCommand(
                () => TestRumbleRequested?.Invoke(this, EventArgs.Empty),
                () => IsDeviceOnline);

        public event EventHandler TestRumbleRequested;

        /// <summary>Raised to test only the left motor.</summary>
        public event EventHandler TestLeftMotorRequested;
        public void FireTestLeftMotor() => TestLeftMotorRequested?.Invoke(this, EventArgs.Empty);

        /// <summary>Raised to test only the right motor.</summary>
        public event EventHandler TestRightMotorRequested;
        public void FireTestRightMotor() => TestRightMotorRequested?.Invoke(this, EventArgs.Empty);

        /// <summary>Raised to test only the left impulse trigger motor.</summary>
        public event EventHandler TestLeftImpulseTriggerRequested;
        public void FireTestLeftImpulseTrigger() => TestLeftImpulseTriggerRequested?.Invoke(this, EventArgs.Empty);

        /// <summary>Raised to test only the right impulse trigger motor.</summary>
        public event EventHandler TestRightImpulseTriggerRequested;
        public void FireTestRightImpulseTrigger() => TestRightImpulseTriggerRequested?.Invoke(this, EventArgs.Empty);

        private RelayCommand _testLeftImpulseTriggerCommand;
        public RelayCommand TestLeftImpulseTriggerCommand =>
            _testLeftImpulseTriggerCommand ??= new RelayCommand(
                () => TestLeftImpulseTriggerRequested?.Invoke(this, EventArgs.Empty),
                () => IsDeviceOnline);

        private RelayCommand _testRightImpulseTriggerCommand;
        public RelayCommand TestRightImpulseTriggerCommand =>
            _testRightImpulseTriggerCommand ??= new RelayCommand(
                () => TestRightImpulseTriggerRequested?.Invoke(this, EventArgs.Empty),
                () => IsDeviceOnline);

        /// <summary>
        /// The TargetSettingName of the mapping currently being recorded
        /// (single-click or Map All). Null when idle. Used to drive
        /// controller-tab element flashing.
        /// </summary>
        private string _currentRecordingTarget;
        public string CurrentRecordingTarget
        {
            get => _currentRecordingTarget;
            set => SetProperty(ref _currentRecordingTarget, value);
        }

        private RelayCommand _clearMappingsCommand;
        public RelayCommand ClearMappingsCommand =>
            _clearMappingsCommand ??= new RelayCommand(ClearAllMappings);

        private void ClearAllMappings()
        {
            foreach (var m in Mappings)
            {
                m.SourceDescriptor = string.Empty;
                m.NegSourceDescriptor = string.Empty;
                m.IsInverted = false;
                m.IsHalfAxis = false;
                m.MappingDeadZone = 50;
                // Drop the device-origin tag too — leaving the GUID
                // behind would surface a stale subtitle and make the
                // next picker selection inherit the wrong device.
                m.PrimarySourceDeviceGuid = string.Empty;
                m.PrimarySourceDeviceLabel = string.Empty;
                // Multi-source rows: clear extra sources AND the
                // combine + custom-formula state so the cleared row
                // truly looks brand-new (was leaving extras + the
                // user's formula behind, which then re-promoted via
                // the merge as soon as the user added a primary).
                m.ExtraSources.Clear();
                m.CombineMode = string.Empty;
                m.CombineExpression = string.Empty;
                m.TrimDeadzone = 25;
                m.TrimRate = 100;
                m.TrimResetOnRelease = true;
                m.SyncSelectedInputFromDescriptor();
            }
        }

        // ── Copy / Paste / Copy From ──

        /// <summary>
        /// Raised when the user wants to copy the current device's settings to the clipboard.
        /// MainWindow/InputService handles reading the PadSetting and calling ToJson().
        /// </summary>
        public event EventHandler CopySettingsRequested;

        private RelayCommand _copySettingsCommand;
        public RelayCommand CopySettingsCommand =>
            _copySettingsCommand ??= new RelayCommand(
                () => CopySettingsRequested?.Invoke(this, EventArgs.Empty),
                () => HasSelectedDevice);

        /// <summary>
        /// Raised when the user wants to paste settings from the clipboard.
        /// MainWindow/InputService handles parsing and applying.
        /// </summary>
        public event EventHandler PasteSettingsRequested;

        private RelayCommand _pasteSettingsCommand;
        public RelayCommand PasteSettingsCommand =>
            _pasteSettingsCommand ??= new RelayCommand(
                () => PasteSettingsRequested?.Invoke(this, EventArgs.Empty),
                () => HasSelectedDevice);

        /// <summary>
        /// Raised when the user wants to copy settings from another device.
        /// MainWindow handles showing the picker dialog.
        /// </summary>
        public event EventHandler CopyFromRequested;

        private RelayCommand _copyFromCommand;
        public RelayCommand CopyFromCommand =>
            _copyFromCommand ??= new RelayCommand(
                () => CopyFromRequested?.Invoke(this, EventArgs.Empty),
                () => HasSelectedDevice);

        // ── Map All ──

        /// <summary>Raised to request recording for the current Map All item.</summary>
        public event EventHandler<MappingItem> MapAllRecordRequested;

        /// <summary>Raised to cancel an in-progress Map All recording.</summary>
        public event EventHandler MapAllCancelRequested;

        private bool _isMapAllActive;
        public bool IsMapAllActive
        {
            get => _isMapAllActive;
            set
            {
                if (SetProperty(ref _isMapAllActive, value))
                {
                    OnPropertyChanged(nameof(MapAllButtonText));
                    OnPropertyChanged(nameof(MapAllButtonTooltip));
                    _mapAllCommand?.NotifyCanExecuteChanged();
                    _stopMapAllCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        public string MapAllButtonText =>
            IsMapAllActive ? Strings.Instance.Common_Stop : Strings.Instance.Pad_MapAll;

        public string MapAllButtonTooltip =>
            IsMapAllActive ? Strings.Instance.Common_Stop : Strings.Instance.Pad_MapAllOneByOne;

        private RelayCommand _stopMapAllCommand;
        public RelayCommand StopMapAllCommand =>
            _stopMapAllCommand ??= new RelayCommand(
                () => MapAllCancelRequested?.Invoke(this, EventArgs.Empty),
                () => IsMapAllActive);

        /// <summary>When true, the current Map All step is recording the negative direction of an axis.</summary>
        internal bool MapAllRecordingNeg { get; set; }

        private int _mapAllCurrentIndex;
        public int MapAllCurrentIndex
        {
            get => _mapAllCurrentIndex;
            set => SetProperty(ref _mapAllCurrentIndex, value);
        }

        private string _mapAllCurrentTarget;
        public string MapAllCurrentTarget
        {
            get => _mapAllCurrentTarget;
            set => SetProperty(ref _mapAllCurrentTarget, value);
        }

        private string _mapAllPromptText;
        /// <summary>Descriptive text shown on the Controller tab during Map All (e.g., "Press: A").</summary>
        public string MapAllPromptText
        {
            get => _mapAllPromptText;
            set => SetProperty(ref _mapAllPromptText, value);
        }

        /// <summary>Timer used to add a short delay between Map All entries.</summary>
        private DispatcherTimer _mapAllDelayTimer;

        private RelayCommand _mapAllCommand;
        public RelayCommand MapAllCommand =>
            _mapAllCommand ??= new RelayCommand(StartMapAll, () => HasSelectedDevice && !IsMapAllActive && SelectedMappedDevice?.IsOnline == true);

        private void StartMapAll()
        {
            if (Mappings.Count == 0) return;

            // Ground-truth online recheck at click time. The command
            // predicate reads MappedDeviceInfo.IsOnline, which refreshes on
            // the dashboard lane (1 Hz-gated while the app is backgrounded),
            // and a poll-read failure marks UserDevice.IsOnline false
            // without raising DevicesUpdated. A click that reactivates the
            // window can therefore pass a stale CanExecute; starting anyway
            // walks every mapping into the recorder's no-device timeout.
            var selGuid = SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;
            if (selGuid != Guid.Empty)
            {
                bool live = false;
                var devs = PadForge.Common.Input.SettingsManager.UserDevices;
                if (devs?.Items != null)
                {
                    lock (devs.SyncRoot)
                    {
                        foreach (var d in devs.Items)
                            if (d != null && d.InstanceGuid == selGuid && d.IsOnline)
                            { live = true; break; }
                    }
                }
                if (!live) return;
            }

            IsMapAllActive = true;
            MapAllCurrentIndex = 0;
            _mapAllCommand?.NotifyCanExecuteChanged();
            AdvanceMapAll();
        }

        private void AdvanceMapAll()
        {
            if (!IsMapAllActive) return;

            if (MapAllCurrentIndex >= Mappings.Count)
            {
                StopMapAll();
                return;
            }

            var mapping = Mappings[MapAllCurrentIndex];

            // Skip non-recordable categories (touchpad inputs can't be
            // isolated by touch) and rows opted out of the bulk Map All
            // walk (e.g., the optional Xbox Series Share button — visible
            // and individually mappable but not in the default sequence).
            if (!mapping.IsRecordable || !mapping.IncludeInMapAll)
            {
                MapAllCurrentIndex++;
                AdvanceMapAll();
                return;
            }

            // Switch to Controller tab (index 0) for stick axes so the 3D arrow is visible.
            // The 3D model is only on the Controller tab; if Map All was started from the
            // Mappings tab, the user wouldn't see the directional arrows otherwise.
            if (mapping.HasNegDirection)
                SelectedConfigTab = 0;

            // Detect Y axis: standard controllers use "AxisY" in the setting name,
            // custom Extended uses "Stick N Y" in the label (setting name is "ExtendedAxisN").
            bool isYAxis = mapping.TargetSettingName.Contains("AxisY")
                        || mapping.TargetLabel.EndsWith(" Y", StringComparison.Ordinal);

            if (MapAllRecordingNeg)
            {
                // Second phase: opposite direction from the first.
                // X: second=left (neg). Y: second=down (pos, because NegateAxis inverts).
                // Keep MapAllRecordingNeg=true until MapAllRecordRequested fires, so the
                // handler can distinguish Y second phase from Y first phase.
                string dirHint = isYAxis ? "(\u2193)" : "(\u2190)";
                // Y: second phase targets pos descriptor (down in game).
                // X: second phase targets neg descriptor (left).
                string target = isYAxis ? mapping.TargetSettingName : mapping.NegSettingName;
                MapAllCurrentTarget = target;
                CurrentRecordingTarget = target;
                MapAllPromptText = string.Format(Strings.Instance.Pad_MapPrompt_Format, $"{mapping.TargetLabel} {dirHint}") + $"  ({MapAllCurrentIndex + 1}/{Mappings.Count})";
            }
            else
            {
                string suffix = "";
                if (mapping.HasNegDirection)
                {
                    // First phase: natural primary direction.
                    // X: first=right (pos). Y: first=up (neg, because NegateAxis inverts).
                    suffix = isYAxis ? " (\u2191)" : " (\u2192)";
                }
                // Y: first phase targets neg descriptor (up in game).
                // X: first phase targets pos descriptor (right).
                string target = (mapping.HasNegDirection && isYAxis) ? mapping.NegSettingName : mapping.TargetSettingName;
                MapAllCurrentTarget = target;
                CurrentRecordingTarget = target;
                MapAllPromptText = string.Format(Strings.Instance.Pad_MapPrompt_Format, $"{mapping.TargetLabel}{suffix}") + $"  ({MapAllCurrentIndex + 1}/{Mappings.Count})";
            }
            MapAllRecordRequested?.Invoke(this, mapping);

            // Clear after firing so OnMapAllItemCompleted will advance the index
            // when the second-phase recording finishes.
            if (MapAllRecordingNeg)
                MapAllRecordingNeg = false;
        }

        /// <summary>Called when a Map All recording completes (success or timeout). Advances to next after a short delay.</summary>
        public void OnMapAllItemCompleted()
        {
            if (!IsMapAllActive) return;

            // Short delay so analog input (axis return to center) doesn't
            // accidentally trigger the next recording.
            _mapAllDelayTimer?.Stop();
            _mapAllDelayTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _mapAllDelayTimer.Tick += (s, e) =>
            {
                _mapAllDelayTimer.Stop();
                _mapAllDelayTimer = null;
                if (!IsMapAllActive) return;

                // MapAllRecordingNeg=true means the first phase just finished and
                // a second phase is needed at the same index.  Stay on the same
                // mapping and let AdvanceMapAll show the opposite-direction prompt.
                if (!MapAllRecordingNeg)
                {
                    MapAllCurrentIndex++;
                }
                AdvanceMapAll();
            };
            _mapAllDelayTimer.Start();
        }

        public void StopMapAll()
        {
            _mapAllDelayTimer?.Stop();
            _mapAllDelayTimer = null;
            IsMapAllActive = false;
            MapAllRecordingNeg = false;
            MapAllCurrentTarget = null;
            CurrentRecordingTarget = null;
            MapAllPromptText = null;
            MapAllCancelRequested?.Invoke(this, EventArgs.Empty);
            _mapAllCommand?.NotifyCanExecuteChanged();
        }

        // ═══════════════════════════════════════════════
        //  State update (30Hz from InputService)
        // ═══════════════════════════════════════════════

        public void UpdateFromEngineState(Gamepad gp, Engine.Vibration vibration, Engine.Vibration selectedDeviceVibration = null)
        {
            ButtonA = gp.IsButtonPressed(Gamepad.A);
            ButtonB = gp.IsButtonPressed(Gamepad.B);
            ButtonX = gp.IsButtonPressed(Gamepad.X);
            ButtonY = gp.IsButtonPressed(Gamepad.Y);
            LeftShoulder = gp.IsButtonPressed(Gamepad.LEFT_SHOULDER);
            RightShoulder = gp.IsButtonPressed(Gamepad.RIGHT_SHOULDER);
            ButtonBack = gp.IsButtonPressed(Gamepad.BACK);
            ButtonStart = gp.IsButtonPressed(Gamepad.START);
            LeftThumbButton = gp.IsButtonPressed(Gamepad.LEFT_THUMB);
            RightThumbButton = gp.IsButtonPressed(Gamepad.RIGHT_THUMB);
            ButtonGuide = gp.IsButtonPressed(Gamepad.GUIDE);
            ButtonShare = gp.Share;
            DPadUp = gp.IsButtonPressed(Gamepad.DPAD_UP);
            DPadDown = gp.IsButtonPressed(Gamepad.DPAD_DOWN);
            DPadLeft = gp.IsButtonPressed(Gamepad.DPAD_LEFT);
            DPadRight = gp.IsButtonPressed(Gamepad.DPAD_RIGHT);

            RawLeftTrigger = gp.LeftTrigger;
            RawRightTrigger = gp.RightTrigger;
            LeftTrigger = gp.LeftTrigger / 65535.0;
            RightTrigger = gp.RightTrigger / 65535.0;

            RawThumbLX = gp.ThumbLX;
            RawThumbLY = gp.ThumbLY;
            RawThumbRX = gp.ThumbRX;
            RawThumbRY = gp.ThumbRY;
            ThumbLX = (gp.ThumbLX - (double)short.MinValue) / 65535.0;
            ThumbLY = 1.0 - ((gp.ThumbLY - (double)short.MinValue) / 65535.0);
            ThumbRX = (gp.ThumbRX - (double)short.MinValue) / 65535.0;
            ThumbRY = 1.0 - ((gp.ThumbRY - (double)short.MinValue) / 65535.0);

            if (vibration != null)
            {
                // FinalVibrationStates — slot-wide max-across-all-devices,
                // each scaled by its own PadSetting. Feeds the Controller-
                // preview-tab motor bar.
                LeftMotorDisplay = vibration.LeftMotorSpeed / 65535.0;
                RightMotorDisplay = vibration.RightMotorSpeed / 65535.0;
            }

            if (selectedDeviceVibration != null)
            {
                // SelectedDeviceVibrationStates — the FFB-tab dropdown's
                // selected device's own scaled output. Feeds the FFB-tab
                // motor bar (device-specific by design).
                DeviceLeftMotorDisplay = selectedDeviceVibration.LeftMotorSpeed / 65535.0;
                DeviceRightMotorDisplay = selectedDeviceVibration.RightMotorSpeed / 65535.0;
                DeviceLeftTriggerMotorDisplay = selectedDeviceVibration.LeftTriggerMotorSpeed / 65535.0;
                DeviceRightTriggerMotorDisplay = selectedDeviceVibration.RightTriggerMotorSpeed / 65535.0;
            }
        }

        /// <summary>
        /// Mirrors the per-slot combined touchpad state to the VM so the
        /// 2D / 3D / web previews can render finger dots and the click
        /// highlight. Only meaningful for PlayStation slots; harmless on
        /// other slot types (state stays at zero / false).
        /// </summary>
        public void UpdateFromTouchpadState(in Engine.TouchpadState tp)
        {
            TouchpadFinger0X = tp.X0;
            TouchpadFinger0Y = tp.Y0;
            TouchpadFinger0Down = tp.Down0;
            TouchpadFinger1X = tp.X1;
            TouchpadFinger1Y = tp.Y1;
            TouchpadFinger1Down = tp.Down1;
            TouchpadClickPressed = tp.Click;
        }

        /// <summary>
        /// Updates per-device stick/trigger values for the stick and trigger tab previews.
        /// Shows only the selected device's input, not the combined slot.
        /// Also syncs live values to StickConfigs/TriggerConfigs items.
        /// </summary>
        public void UpdateDeviceState(Gamepad gp)
        {
            DeviceRawLeftTrigger = gp.LeftTrigger;
            DeviceRawRightTrigger = gp.RightTrigger;
            DeviceLeftTrigger = gp.LeftTrigger / 65535.0;
            DeviceRightTrigger = gp.RightTrigger / 65535.0;

            DeviceRawThumbLX = gp.ThumbLX;
            DeviceRawThumbLY = gp.ThumbLY;
            DeviceRawThumbRX = gp.ThumbRX;
            DeviceRawThumbRY = gp.ThumbRY;
            DeviceThumbLX = (gp.ThumbLX - (double)short.MinValue) / 65535.0;
            DeviceThumbLY = 1.0 - ((gp.ThumbLY - (double)short.MinValue) / 65535.0);
            DeviceThumbRX = (gp.ThumbRX - (double)short.MinValue) / 65535.0;
            DeviceThumbRY = 1.0 - ((gp.ThumbRY - (double)short.MinValue) / 65535.0);

            // Sync live values to dynamic config items (full processing pipeline for preview)
            if (StickConfigs.Count > 0)
            {
                var (lvx, lox, lvy, loy) = ProcessStickForPreview(
                    DeviceThumbLX + LeftCenterOffsetX / 200.0,
                    DeviceThumbLY - LeftCenterOffsetY / 200.0,
                    LeftDeadZoneX, LeftDeadZoneY,
                    LeftAntiDeadZoneX, LeftAntiDeadZoneY,
                    LeftLinear, LeftMaxRangeX, LeftMaxRangeY,
                    LeftMaxRangeXNeg, LeftMaxRangeYNeg,
                    LeftSensitivityCurveX, LeftSensitivityCurveY,
                    (DeadZoneShape)LeftDeadZoneShape,
                    Common.StickBoundary.GetOrBuild(LeftThumbBoundaryMap));
                StickConfigs[0].LiveX = lvx;
                StickConfigs[0].LiveY = lvy;
                StickConfigs[0].RawX = (short)Math.Clamp((lox - 0.5) * 2.0 * 32767, short.MinValue, short.MaxValue);
                StickConfigs[0].RawY = (short)Math.Clamp((0.5 - loy) * 2.0 * 32767, short.MinValue, short.MaxValue);
                // The cold input dot shows the RAW hardware position, pre-pipeline.
                // The center offset belongs only to the ProcessStickForPreview input
                // above (offset-before-deadzone, matching Step 3); baking it into
                // the raw dot too erased the calibration gap the two dots exist to
                // show, and dragged the input dot around with the offset sliders.
                StickConfigs[0].RawPosX = DeviceThumbLX;
                StickConfigs[0].RawPosY = DeviceThumbLY;
                StickConfigs[0].HardwareRawX = gp.ThumbLX;
                StickConfigs[0].HardwareRawY = gp.ThumbLY;
                UpdateStickCurveDots(StickConfigs[0], DeviceThumbLX, DeviceThumbLY);
            }
            if (StickConfigs.Count > 1)
            {
                var (rvx, rox, rvy, roy) = ProcessStickForPreview(
                    DeviceThumbRX + RightCenterOffsetX / 200.0,
                    DeviceThumbRY - RightCenterOffsetY / 200.0,
                    RightDeadZoneX, RightDeadZoneY,
                    RightAntiDeadZoneX, RightAntiDeadZoneY,
                    RightLinear, RightMaxRangeX, RightMaxRangeY,
                    RightMaxRangeXNeg, RightMaxRangeYNeg,
                    RightSensitivityCurveX, RightSensitivityCurveY,
                    (DeadZoneShape)RightDeadZoneShape,
                    Common.StickBoundary.GetOrBuild(RightThumbBoundaryMap));
                StickConfigs[1].LiveX = rvx;
                StickConfigs[1].LiveY = rvy;
                StickConfigs[1].RawX = (short)Math.Clamp((rox - 0.5) * 2.0 * 32767, short.MinValue, short.MaxValue);
                StickConfigs[1].RawY = (short)Math.Clamp((0.5 - roy) * 2.0 * 32767, short.MinValue, short.MaxValue);
                // Raw dot: no offset, same as the left stick above.
                StickConfigs[1].RawPosX = DeviceThumbRX;
                StickConfigs[1].RawPosY = DeviceThumbRY;
                StickConfigs[1].HardwareRawX = gp.ThumbRX;
                StickConfigs[1].HardwareRawY = gp.ThumbRY;
                UpdateStickCurveDots(StickConfigs[1], DeviceThumbRX, DeviceThumbRY);
            }
            if (TriggerConfigs.Count > 0)
            {
                var processed = ProcessTriggerForPreview(DeviceLeftTrigger, TriggerConfigs[0]);
                TriggerConfigs[0].LiveValue = processed;
                TriggerConfigs[0].RawNorm = DeviceLeftTrigger;
                TriggerConfigs[0].RawValue = (ushort)Math.Clamp((int)(DeviceLeftTrigger * 65535), 0, 65535);
                UpdateTriggerCurveDot(TriggerConfigs[0], DeviceLeftTrigger);
            }
            if (TriggerConfigs.Count > 1)
            {
                var processed = ProcessTriggerForPreview(DeviceRightTrigger, TriggerConfigs[1]);
                TriggerConfigs[1].LiveValue = processed;
                TriggerConfigs[1].RawNorm = DeviceRightTrigger;
                TriggerConfigs[1].RawValue = (ushort)Math.Clamp((int)(DeviceRightTrigger * 65535), 0, 65535);
                UpdateTriggerCurveDot(TriggerConfigs[1], DeviceRightTrigger);
            }
        }

        /// <summary>
        /// Processes both stick axes together through the shape-aware deadzone pipeline.
        /// Uses the same algorithms as Step3's ApplyDeadZone for preview consistency.
        /// </summary>
        private static (double visualX, double outputX, double visualY, double outputY)
            ProcessStickForPreview(
                double adjNormX, double adjNormY,
                double deadZoneX, double deadZoneY,
                double antiDeadZoneX, double antiDeadZoneY,
                double linear, double maxRangeX, double maxRangeY,
                double maxRangeXNeg, double maxRangeYNeg,
                string curveX, string curveY,
                DeadZoneShape shape,
                double[] boundaryLut = null,
                bool boundaryMapInInputFrame = false)
        {
            // Convert to signed [-1, 1]
            double sx = (adjNormX - 0.5) * 2.0;
            double sy = (adjNormY - 0.5) * 2.0;
            // #174: circular reshape BEFORE the dead zone, matching Step 3's
            // order, so the preview OUT dot shows the same warp the game gets.
            // The gamepad preview's sy is screen-down; the boundary map is
            // captured in the XInput up-positive frame, so flip Y around the
            // warp to query the map in its own frame (an asymmetric gate would
            // otherwise mirror about the X axis). The Extended path captures
            // and previews in the same raw.Axes frame, so its caller sets
            // boundaryMapInInputFrame to skip the flip (which would render an
            // asymmetric gate vertically mirrored there).
            if (boundaryLut != null)
            {
                if (boundaryMapInInputFrame)
                {
                    Common.StickBoundary.ReshapeUnit(ref sx, ref sy, boundaryLut);
                }
                else
                {
                    double wy = -sy;
                    Common.StickBoundary.ReshapeUnit(ref sx, ref wy, boundaryLut);
                    sy = -wy;
                }
            }
            double signX = Math.Sign(sx), signY = Math.Sign(sy);
            double magX = Math.Abs(sx), magY = Math.Abs(sy);
            double dzXn = deadZoneX / 100.0, dzYn = deadZoneY / 100.0;
            // Pick max range based on direction of input (mirrors Step3 pipeline).
            double mrXn = (sx >= 0 ? maxRangeX : maxRangeXNeg) / 100.0;
            double mrYn = (sy >= 0 ? maxRangeY : maxRangeYNeg) / 100.0;
            if (mrXn <= dzXn) mrXn = Math.Min(dzXn + 0.01, 1.0);
            if (mrYn <= dzYn) mrYn = Math.Min(dzYn + 0.01, 1.0);

            // ── Axial: cross-shaped DZ visualization ──
            if (shape == DeadZoneShape.Axial)
            {
                bool xInDz = magX < dzXn, yInDz = magY < dzYn;

                // Center rectangle (both in DZ) → dot at center.
                if (xInDz && yInDz)
                    return (0.5, 0.5, 0.5, 0.5);

                // Per-axis DZ gate + rescale (mirrors ApplySingleDeadZone).
                double remAx = xInDz ? 0 : Math.Min((magX - dzXn) / (mrXn - dzXn), 1.0);
                double remAy = yInDz ? 0 : Math.Min((magY - dzYn) / (mrYn - dzYn), 1.0);
                double oAx = PostDzForPreview(remAx, curveX, antiDeadZoneX, linear);
                double oAy = PostDzForPreview(remAy, curveY, antiDeadZoneY, linear);

                double outPosX = Math.Clamp(0.5 + signX * oAx * 0.5, 0.0, 1.0);
                double outPosY = Math.Clamp(0.5 + signY * oAy * 0.5, 0.0, 1.0);

                // Visual: each axis jumps to its DZ boundary, scales outward.
                // In the cross arms, the zeroed axis stays at center (snapped to axis).
                // In the corners, both jump to boundary.
                double visAx = xInDz ? 0.0 : dzXn + oAx * (1.0 - dzXn);
                double visAy = yInDz ? 0.0 : dzYn + oAy * (1.0 - dzYn);
                double visPosX = xInDz ? 0.5 : Math.Clamp(0.5 + signX * visAx * 0.5, 0.0, 1.0);
                double visPosY = yInDz ? 0.5 : Math.Clamp(0.5 + signY * visAy * 0.5, 0.0, 1.0);

                return (visPosX, outPosX, visPosY, outPosY);
            }

            // ── 2D shapes (Radial, Sloped, Hybrid) ──
            double remX, remY;
            switch (shape)
            {
                case DeadZoneShape.Radial:
                    Common.Input.InputManager.ComputeRadial(sx, sy, magX, magY, dzXn, dzYn, mrXn, mrYn, false, out remX, out remY);
                    break;
                case DeadZoneShape.ScaledRadial:
                    Common.Input.InputManager.ComputeRadial(sx, sy, magX, magY, dzXn, dzYn, mrXn, mrYn, true, out remX, out remY);
                    break;
                case DeadZoneShape.SlopedAxial:
                    Common.Input.InputManager.ComputeSloped(magX, magY, dzXn, dzYn, mrXn, mrYn, false, out remX, out remY);
                    break;
                case DeadZoneShape.SlopedScaledAxial:
                    Common.Input.InputManager.ComputeSloped(magX, magY, dzXn, dzYn, mrXn, mrYn, true, out remX, out remY);
                    break;
                case DeadZoneShape.Hybrid:
                    Common.Input.InputManager.ComputeHybrid(sx, sy, magX, magY, dzXn, dzYn, mrXn, mrYn, out remX, out remY, out signX, out signY);
                    break;
                default:
                    remX = magX; remY = magY;
                    break;
            }

            // Post-DZ per axis: curve → ADZ → linear (mirrors ApplyPostDeadZone)
            double outX = PostDzForPreview(remX, curveX, antiDeadZoneX, linear);
            double outY = PostDzForPreview(remY, curveY, antiDeadZoneY, linear);

            double outputPosX = Math.Clamp(0.5 + signX * outX * 0.5, 0.0, 1.0);
            double outputPosY = Math.Clamp(0.5 + signY * outY * 0.5, 0.0, 1.0);

            // ── Shape-specific visual mapping ──
            // Principle: dot at center inside red zones, axis-constrained in yellow zones,
            // and jumps to zone boundary when exiting (never appears inside a colored zone).

            const double visEps = 1e-10;

            // ── Sloped Axial (non-scaled): output position directly ──
            // Natural boundary at wedge edge (raw magnitude ≈ effDz at boundary).
            if (shape == DeadZoneShape.SlopedAxial)
            {
                bool xZeroed = magX < dzXn * magY;
                bool yZeroed = magY < dzYn * magX;
                double visX = xZeroed ? 0.5 : outputPosX;
                double visY = yZeroed ? 0.5 : outputPosY;
                return (visX, outputPosX, visY, outputPosY);
            }

            // ── Sloped Scaled Axial: wedge boundary jump ──
            // Rescaled output starts from 0 — jump to wedge edge like Scaled Radial
            // jumps to circle edge.
            if (shape == DeadZoneShape.SlopedScaledAxial)
            {
                bool xZeroed = magX < dzXn * magY;
                bool yZeroed = magY < dzYn * magX;
                double visX, visY;
                if (xZeroed)
                    visX = 0.5;
                else
                {
                    double effDz = dzXn * magY;
                    double vis = effDz + outX * (1.0 - effDz);
                    visX = Math.Clamp(0.5 + signX * vis * 0.5, 0.0, 1.0);
                }
                if (yZeroed)
                    visY = 0.5;
                else
                {
                    double effDz = dzYn * magX;
                    double vis = effDz + outY * (1.0 - effDz);
                    visY = Math.Clamp(0.5 + signY * vis * 0.5, 0.0, 1.0);
                }
                return (visX, outputPosX, visY, outputPosY);
            }

            // ── Scaled Radial: radial boundary jump ──
            if (shape == DeadZoneShape.ScaledRadial)
            {
                double eDzX = Math.Max(dzXn, visEps), eDzY = Math.Max(dzYn, visEps);
                double edx = sx / eDzX, edy = sy / eDzY;
                if (edx * edx + edy * edy < 1.0)
                    return (0.5, outputPosX, 0.5, outputPosY);

                // DZ boundary radius in the direction of the stick.
                double rawMag = Math.Sqrt(magX * magX + magY * magY);
                if (rawMag < visEps)
                    return (0.5, outputPosX, 0.5, outputPosY);
                double ux = magX / rawMag, uy = magY / rawMag;
                double dxu = ux / eDzX, dyu = uy / eDzY;
                double dzR = 1.0 / Math.Sqrt(dxu * dxu + dyu * dyu);

                // Map output magnitude [0,max] → visual [dzR, 1] so dot starts at circle edge.
                double outMag = Math.Sqrt(outX * outX + outY * outY);
                double visMag = dzR + outMag * (1.0 - dzR);

                double visX = Math.Clamp(0.5 + signX * ux * visMag * 0.5, 0.0, 1.0);
                double visY = Math.Clamp(0.5 + signY * uy * visMag * 0.5, 0.0, 1.0);
                return (visX, outputPosX, visY, outputPosY);
            }

            // ── Hybrid: circle (red center) + wedge (yellow axis-snap) ──
            if (shape == DeadZoneShape.Hybrid)
            {
                double eDzX = Math.Max(dzXn, visEps), eDzY = Math.Max(dzYn, visEps);
                double edx = sx / eDzX, edy = sy / eDzY;
                if (edx * edx + edy * edy < 1.0)
                    return (0.5, outputPosX, 0.5, outputPosY);

                // DZ boundary radius in the direction of the stick.
                double rawMag = Math.Sqrt(magX * magX + magY * magY);
                if (rawMag < visEps)
                    return (0.5, outputPosX, 0.5, outputPosY);
                double ux = magX / rawMag, uy = magY / rawMag;
                double dxu = ux / eDzX, dyu = uy / eDzY;
                double dzR = 1.0 / Math.Sqrt(dxu * dxu + dyu * dyu);

                // Check wedge conditions from the sloped stage.
                Common.Input.InputManager.ComputeRadial(sx, sy, magX, magY, dzXn, dzYn, mrXn, mrYn,
                    true, out double srX, out double srY);
                bool xZeroed = srX < dzXn * srY;
                bool yZeroed = srY < dzYn * srX;

                if (xZeroed || yZeroed)
                {
                    // Wedge zone: zeroed axis at center, alive axis jumps to circle edge.
                    double visX = xZeroed ? 0.5
                        : Math.Clamp(0.5 + signX * (dzXn + outX * (1.0 - dzXn)) * 0.5, 0.0, 1.0);
                    double visY = yZeroed ? 0.5
                        : Math.Clamp(0.5 + signY * (dzYn + outY * (1.0 - dzYn)) * 0.5, 0.0, 1.0);
                    return (visX, outputPosX, visY, outputPosY);
                }

                // Free zone: radial boundary jump in stick direction.
                double outMag = Math.Sqrt(outX * outX + outY * outY);
                double visMag = dzR + outMag * (1.0 - dzR);
                double vfX = Math.Clamp(0.5 + signX * ux * visMag * 0.5, 0.0, 1.0);
                double vfY = Math.Clamp(0.5 + signY * uy * visMag * 0.5, 0.0, 1.0);
                return (vfX, outputPosX, vfY, outputPosY);
            }

            // ── Radial (non-scaled): output position directly ──
            // Natural boundary jump: raw magnitude at DZ edge ≈ DZ radius.
            return (outputPosX, outputPosX, outputPosY, outputPosY);
        }

        private static double PostDzForPreview(double remapped, string curveString, double antiDeadZone, double linear)
        {
            if (remapped <= 0 && antiDeadZone <= 0) return 0;
            remapped = StickConfigItem.ApplyCurve(remapped, curveString);
            double adzNorm = antiDeadZone / 100.0;
            double output = adzNorm + remapped * (1.0 - adzNorm);
            if (linear > 0)
            {
                double lf = linear / 100.0;
                output = remapped * lf + output * (1.0 - lf);
            }
            return output;
        }

        /// <summary>
        /// <summary>
        /// Updates the CurveEditor live input values for a stick config item.
        /// normX/normY are 0-1 normalized where 0.5 = center.
        /// CurveEditor handles the dot rendering internally.
        /// </summary>
        private static void UpdateStickCurveDots(StickConfigItem stick, double normX, double normY)
        {
            // Signed input for the CurveEditor LiveInput property
            double signedX = (normX - 0.5) * 2.0;
            double signedY = -((normY - 0.5) * 2.0);
            stick.LiveInputX = signedX;
            stick.LiveInputY = signedY;
        }

        private static void UpdateTriggerCurveDot(TriggerConfigItem trig, double inputNorm)
        {
            trig.LiveInputForCurve = Math.Clamp(inputNorm, 0, 1);
        }

        /// <summary>
        /// Applies the trigger processing pipeline (deadzone, max range, curve, anti-deadzone)
        /// to a raw 0–1 trigger value for preview display. Mirrors Step3's ApplyTriggerDeadZone.
        /// </summary>
        private static double ProcessTriggerForPreview(double rawNorm, TriggerConfigItem trig)
        {
            double t = Math.Clamp(rawNorm, 0, 1);
            double dz = trig.DeadZone / 100.0;
            double mr = trig.MaxRange / 100.0;
            if (mr <= dz) mr = dz + 0.01;

            if (t < dz) return 0;

            double remapped = Math.Min((t - dz) / (mr - dz), 1.0);
            double output = StickConfigItem.ApplyCurve(remapped, trig.SensitivityCurve);

            // Anti-deadzone: offset the output minimum
            double adz = trig.AntiDeadZone / 100.0;
            if (adz > 0)
                output = adz + output * (1.0 - adz);

            return Math.Clamp(output, 0, 1);
        }

        // ═══════════════════════════════════════════════
        //  Extended raw state snapshot (for custom Extended schematic view)
        // ═══════════════════════════════════════════════

        /// <summary>
        /// Latest ExtendedRawState snapshot for custom Extended display.
        /// Updated at 30Hz alongside UpdateFromEngineState.
        /// </summary>
        public ExtendedRawState ExtendedOutputSnapshot { get; private set; }

        /// <summary>
        /// Latest KbmRawState snapshot for KBM preview display.
        /// Updated at 30Hz alongside UpdateFromEngineState.
        /// </summary>
        public KbmRawState KbmOutputSnapshot { get; set; }

        /// <summary>
        /// Stores the combined virtual-controller output for the Extended
        /// schematic preview. Writes <see cref="ExtendedOutputSnapshot"/>
        /// only — does not touch <see cref="StickConfigs"/> or
        /// <see cref="TriggerConfigs"/>, since those are driven by the
        /// per-device <see cref="UpdateFromExtendedDeviceState"/> path so
        /// the per-stick / per-trigger debug tabs can show device-specific
        /// values without colliding with the schematic's combined view.
        /// Pre-split this method also wrote the StickConfigs/TriggerConfigs
        /// live values, which the per-device update path then overwrote on
        /// the same tick — leaving the schematic showing per-device data
        /// when a device was selected. Mirrors the Xbox split between
        /// <see cref="UpdateFromEngineState"/> (combined, schematic) and
        /// <see cref="UpdateDeviceState"/> (per-device, stick tab).
        /// </summary>
        public void UpdateFromExtendedRawState(ExtendedRawState raw)
        {
            ExtendedOutputSnapshot = raw;
            OnPropertyChanged(nameof(ExtendedOutputSnapshot));
        }

        /// <summary>
        /// Updates per-stick and per-trigger live values for the Extended
        /// stick/trigger debug tabs. Caller passes either the selected
        /// device's <c>ExtendedRawOutputState</c> or, when no device is
        /// selected, the combined slot raw state as a fallback so the tabs
        /// always show something. Does NOT write
        /// <see cref="ExtendedOutputSnapshot"/> — that field is owned by
        /// the schematic preview path.
        /// </summary>
        public void UpdateFromExtendedDeviceState(ExtendedRawState raw)
        {
            // Sync stick config items from raw axes. Calibration capture and
            // the cold dot must read the PRE-tuning frame: raw.Axes has been
            // through center offset / reshape / deadzone / curve by the time
            // it reaches here, so sampling it made boundary calibration
            // capture its own output (progressively shrinking maps on
            // re-calibration). HardwareAxes is the producer's pre-tuning
            // snapshot; Axes stays the fallback for producers that don't
            // populate it.
            var hwAxes = raw.HardwareAxes ?? raw.Axes;
            foreach (var stick in StickConfigs)
            {
                bool hasX = stick.AxisXIndex >= 0 && hwAxes != null && stick.AxisXIndex < hwAxes.Length;
                bool hasY = stick.AxisYIndex >= 0 && hwAxes != null && stick.AxisYIndex < hwAxes.Length;

                if (hasX) stick.HardwareRawX = hwAxes[stick.AxisXIndex];
                if (hasY) stick.HardwareRawY = hwAxes[stick.AxisYIndex];

                double normX = hasX ? (hwAxes[stick.AxisXIndex] - (double)short.MinValue) / 65535.0 : 0.5;
                double normY = hasY ? (hwAxes[stick.AxisYIndex] - (double)short.MinValue) / 65535.0 : 0.5;

                // Cold input dot: raw hardware position, no offset (sibling of
                // the gamepad path in UpdateDeviceState). This path never wrote
                // RawPosX/Y, leaving Extended stick tabs with a frozen raw dot.
                if (hasX) stick.RawPosX = normX;
                if (hasY) stick.RawPosY = normY;

                var (vx, ox, vy, oy) = ProcessStickForPreview(
                    normX + stick.CenterOffsetX / 200.0,
                    normY - stick.CenterOffsetY / 200.0,
                    stick.DeadZoneX, stick.DeadZoneY,
                    stick.AntiDeadZoneX, stick.AntiDeadZoneY,
                    stick.Linear, stick.MaxRangeX, stick.MaxRangeY,
                    stick.MaxRangeXNeg, stick.MaxRangeYNeg,
                    stick.SensitivityCurveX, stick.SensitivityCurveY,
                    stick.DeadZoneShape,
                    // Empty for custom sticks 2+ (never calibrated), so this is a
                    // no-op there, matching the runtime's sticks-0/1-only warp.
                    Common.StickBoundary.GetOrBuild(stick.BoundaryMap),
                    // Extended capture (HardwareRawX/Y) and this preview input
                    // share the raw.Axes frame; no screen-down flip applies.
                    boundaryMapInInputFrame: true);

                if (hasX) { stick.LiveX = vx; stick.RawX = (short)Math.Clamp((ox - 0.5) * 2.0 * 32767, short.MinValue, short.MaxValue); }
                if (hasY) { stick.LiveY = vy; stick.RawY = (short)Math.Clamp((0.5 - oy) * 2.0 * 32767, short.MinValue, short.MaxValue); }

                UpdateStickCurveDots(stick, stick.LiveX, stick.LiveY);
            }

            // Sync trigger config items from raw axes
            foreach (var trig in TriggerConfigs)
            {
                if (trig.AxisIndex >= 0 && raw.Axes != null && trig.AxisIndex < raw.Axes.Length)
                {
                    // Trigger axes are signed short (-32768..32767), normalize to 0.0-1.0
                    double rawNorm = (raw.Axes[trig.AxisIndex] - (double)short.MinValue) / 65535.0;
                    var processed = ProcessTriggerForPreview(rawNorm, trig);
                    trig.LiveValue = processed;
                    trig.RawNorm = rawNorm;
                    trig.RawValue = (ushort)Math.Clamp((int)(rawNorm * 65535), 0, 65535);
                    UpdateTriggerCurveDot(trig, rawNorm);
                }
            }
        }

        // ═══════════════════════════════════════════════
        //  MIDI raw state snapshot (for MIDI preview view)
        // ═══════════════════════════════════════════════

        public MidiRawState MidiOutputSnapshot { get; private set; }

        public void UpdateFromMidiRawState(MidiRawState raw)
        {
            MidiOutputSnapshot = raw;
            OnPropertyChanged(nameof(MidiOutputSnapshot));
        }

        public void RefreshCommands()
        {
            _testRumbleCommand?.NotifyCanExecuteChanged();
            _soundTestCommand?.NotifyCanExecuteChanged();
            _testLeftImpulseTriggerCommand?.NotifyCanExecuteChanged();
            _testRightImpulseTriggerCommand?.NotifyCanExecuteChanged();
            _removeMacroCommand?.NotifyCanExecuteChanged();
            _copySettingsCommand?.NotifyCanExecuteChanged();
            _pasteSettingsCommand?.NotifyCanExecuteChanged();
            _copyFromCommand?.NotifyCanExecuteChanged();
            _mapAllCommand?.NotifyCanExecuteChanged();
        }
    }
}
