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
            // Strings.Instance.* — without an explicit notification the
            // bindings keep the old language's value until the next
            // IsMapAllActive flip.
            OnPropertyChanged(nameof(MapAllButtonText));
            OnPropertyChanged(nameof(MapAllButtonTooltip));
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
        // <c>_playStationConfig</c> below is a reference to the
        // SelectedMappedDevice's entry in this dictionary, swapped on
        // device change so the Lighting tab's bindings re-resolve.
        // ConcurrentDictionary so the polling thread (which iterates this
        // every dispatch tick + every macro lightbar action) doesn't race
        // with UI-thread mutations from device-map changes / settings
        // load. Iteration on ConcurrentDictionary returns a moment-in-time
        // snapshot rather than throwing InvalidOperationException.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, PlayStationSlotConfig> _perDevicePlayStationConfigs = new();
        private PlayStationSlotConfig _playStationConfig = new();
        private static readonly PlayStationSlotConfig _emptyPlayStationConfigSentinel = new();

        /// <summary>Per-(slot, device) lighting tab configs keyed by
        /// physical device InstanceGuid. Always populated for every
        /// mapped device; the empty Guid key holds a fallback used
        /// before any device is mapped.</summary>
        public IReadOnlyDictionary<Guid, PlayStationSlotConfig> PerDevicePlayStationConfigs
            => _perDevicePlayStationConfigs;

        /// <summary>The lighting tab's currently-bound config —
        /// references the SelectedMappedDevice's entry in
        /// <see cref="PerDevicePlayStationConfigs"/>. The setter accepts
        /// any config (used by load paths to seed the dictionary
        /// before SelectedMappedDevice is set). On swap, the forwarder
        /// re-attaches its PropertyChanged subscription so listeners
        /// of <see cref="ActivePlayStationConfigPropertyChanged"/>
        /// keep receiving events from whichever per-device config is
        /// currently bound.</summary>
        public PlayStationSlotConfig PlayStationConfig
        {
            get => _playStationConfig;
            set
            {
                var old = _playStationConfig;
                if (SetProperty(ref _playStationConfig, value ?? new()))
                {
                    if (old != null) old.PropertyChanged -= OnActivePlayStationConfigPropertyChanged;
                    if (_playStationConfig != null) _playStationConfig.PropertyChanged += OnActivePlayStationConfigPropertyChanged;
                }
            }
        }

        /// <summary>Forwards PropertyChanged from the currently-bound
        /// <see cref="PlayStationConfig"/> regardless of which
        /// per-device entry the anchor points at. Subscribers attach to
        /// this rather than to the inner config so the subscription
        /// follows the anchor across SelectedMappedDevice swaps.</summary>
        public event System.ComponentModel.PropertyChangedEventHandler ActivePlayStationConfigPropertyChanged;

        private void OnActivePlayStationConfigPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
            => ActivePlayStationConfigPropertyChanged?.Invoke(sender, e);

        /// <summary>Returns the per-device lighting config for the given
        /// device, creating a fresh default entry if none exists yet.
        /// Used by macro fan-out and the polling-thread synthesizer to
        /// resolve a specific (slot, device) config.</summary>
        public PlayStationSlotConfig GetOrCreatePlayStationConfig(Guid deviceGuid)
        {
            if (deviceGuid == Guid.Empty)
                return _playStationConfig;
            return _perDevicePlayStationConfigs.GetOrAdd(deviceGuid, _ => new PlayStationSlotConfig());
        }

        /// <summary>Snapshot of every per-device config on the slot. Used
        /// by macro fan-out so a slot-level lightbar action writes to
        /// each device's config in turn.</summary>
        public IEnumerable<PlayStationSlotConfig> EnumeratePlayStationConfigs()
            => _perDevicePlayStationConfigs.Values;

        /// <summary>Ensures the per-device dictionary has an entry for
        /// every currently-mapped device. Newly-mapped devices get a
        /// fresh default config — the user customizes each device's
        /// Lighting tab independently. Called by the input service
        /// after MappedDevices changes.</summary>
        public void EnsurePlayStationConfigsForMappedDevices()
        {
            foreach (var dev in MappedDevices)
            {
                if (dev.InstanceGuid == Guid.Empty) continue;
                _perDevicePlayStationConfigs.GetOrAdd(dev.InstanceGuid, _ => new PlayStationSlotConfig());
            }
        }

        /// <summary>Switches the Lighting tab's bound config to the
        /// device with the given InstanceGuid, creating an entry if
        /// missing. Called by SelectedMappedDevice change.</summary>
        private void BindPlayStationConfigForDevice(Guid deviceGuid)
        {
            PlayStationSlotConfig target;
            if (deviceGuid == Guid.Empty)
            {
                // No device selected — bind a sentinel so the UI doesn't
                // mutate any device's config inadvertently.
                target = _emptyPlayStationConfigSentinel;
            }
            else
            {
                target = GetOrCreatePlayStationConfig(deviceGuid);
            }
            if (!ReferenceEquals(_playStationConfig, target))
                PlayStationConfig = target;
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

            public bool IsOnline
            {
                get => _isOnline;
                set => SetProperty(ref _isOnline, value);
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
                    // Swap the Lighting tab's bound config to the new
                    // device's per-device entry. UI bindings that resolve
                    // PlayStationConfig.* re-evaluate against the new
                    // reference.
                    BindPlayStationConfigForDevice(value?.InstanceGuid ?? Guid.Empty);
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
            BindPlayStationConfigForDevice(_selectedMappedDevice?.InstanceGuid ?? Guid.Empty);
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
                    LayerActivated?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>Raised when <see cref="ActiveLayerMask"/> changes. Subscribers
        /// reload per-row source data so the DataGrid reflects the picked
        /// layer's rows instead of the previous layer's.</summary>
        public event EventHandler LayerActivated;

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
            LayerTabs.Add(new ShiftLayerInfo
            {
                LayerMask = "Base",
                LayerName = PadForge.Resources.Strings.Strings.Instance.Pad_Shift_BaseTabLabel,
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
                Mappings.Add(new MappingItem(Strings.Instance.Mapping_TouchpadX1, "TouchpadX1", MappingCategory.Touchpad));
                Mappings.Add(new MappingItem(Strings.Instance.Mapping_TouchpadY1, "TouchpadY1", MappingCategory.Touchpad));
                Mappings.Add(new MappingItem(Strings.Instance.Mapping_TouchpadX2, "TouchpadX2", MappingCategory.Touchpad));
                Mappings.Add(new MappingItem(Strings.Instance.Mapping_TouchpadY2, "TouchpadY2", MappingCategory.Touchpad));
                Mappings.Add(new MappingItem(Strings.Instance.Mapping_TouchpadContact1, "TouchpadContact1", MappingCategory.Touchpad));
                Mappings.Add(new MappingItem(Strings.Instance.Mapping_TouchpadContact2, "TouchpadContact2", MappingCategory.Touchpad));
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

            // Buttons
            for (int i = 0; i < cfg.ButtonCount; i++)
                Mappings.Add(new MappingItem(string.Format(Strings.Instance.Extended_Button_Format, i + 1), $"ExtendedBtn{i}", MappingCategory.Buttons));

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
                GyroAimEngageButton = "";
                GyroAimEngageDeviceGuid = "";
                GyroAimEngageMode = "Hold";
            });

        private int _forceOverallGain = 100;
        public int ForceOverallGain { get => _forceOverallGain; set => SetProperty(ref _forceOverallGain, Math.Clamp(value, 0, 100)); }

        private int _leftMotorStrength = 100;
        public int LeftMotorStrength { get => _leftMotorStrength; set => SetProperty(ref _leftMotorStrength, Math.Clamp(value, 0, 100)); }

        private int _rightMotorStrength = 100;
        public int RightMotorStrength { get => _rightMotorStrength; set => SetProperty(ref _rightMotorStrength, Math.Clamp(value, 0, 100)); }

        private bool _swapMotors;
        public bool SwapMotors { get => _swapMotors; set => SetProperty(ref _swapMotors, value); }

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
            // at this pad index inherits the deleted slot's macros.
            Macros.Clear();

            // Per-device Lighting tab configs live in this PadViewModel's
            // dictionary, keyed by physical device InstanceGuid — not on
            // UserSetting, so DeleteSlot's UserSetting removal does not
            // reach them. Without this clear, deleting a slot and later
            // remapping the same physical device to a new slot would
            // resurrect that device's prior lightbar mode / colors /
            // palette via GetOrAdd in EnsurePlayStationConfigsForMappedDevices,
            // and the saved XML would still carry the stale entries.
            _perDevicePlayStationConfigs.Clear();
            PlayStationConfig = new PlayStationSlotConfig();
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
                var item = new StickConfigItem(i, title, xiIdx, yiIdx, iconLabel);
                SyncStickItemFromVm(item);
                item.PropertyChanged += OnStickConfigPropertyChanged;
                StickConfigs.Add(item);
            }
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
                    }
                    if (isConfigProp) ConfigItemDirtyCallback?.Invoke();
                    break;
                default:
                    // Extended custom sticks 2+: values stored directly on ConfigItem,
                    // persisted via SettingsService.UpdatePadSettingsFromViewModels.
                    if (isConfigProp) ConfigItemDirtyCallback?.Invoke();
                    break;
            }
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
                    ButtonStyle = MacroButtonNames.DeriveStyle(_outputType)
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

        /// <summary>
        /// Syncs macro button display style to all macros when the output
        /// controller type changes.
        /// </summary>
        private void SyncMacroButtonStyle()
        {
            var style = MacroButtonNames.DeriveStyle(_outputType);
            int btnCount = (_outputType == VirtualControllerType.Extended ? _extendedConfig?.ButtonCount : null) ?? 11;
            foreach (var macro in Macros)
            {
                macro.ButtonStyle = style;
                macro.CustomButtonCount = btnCount;
                foreach (var action in macro.Actions)
                    action.CustomButtonCount = btnCount;
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
            set => SetProperty(ref _selectedConfigTab, value);
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
                    (DeadZoneShape)LeftDeadZoneShape);
                StickConfigs[0].LiveX = lvx;
                StickConfigs[0].LiveY = lvy;
                StickConfigs[0].RawX = (short)Math.Clamp((lox - 0.5) * 2.0 * 32767, short.MinValue, short.MaxValue);
                StickConfigs[0].RawY = (short)Math.Clamp((0.5 - loy) * 2.0 * 32767, short.MinValue, short.MaxValue);
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
                    (DeadZoneShape)RightDeadZoneShape);
                StickConfigs[1].LiveX = rvx;
                StickConfigs[1].LiveY = rvy;
                StickConfigs[1].RawX = (short)Math.Clamp((rox - 0.5) * 2.0 * 32767, short.MinValue, short.MaxValue);
                StickConfigs[1].RawY = (short)Math.Clamp((0.5 - roy) * 2.0 * 32767, short.MinValue, short.MaxValue);
                StickConfigs[1].HardwareRawX = gp.ThumbRX;
                StickConfigs[1].HardwareRawY = gp.ThumbRY;
                UpdateStickCurveDots(StickConfigs[1], DeviceThumbRX, DeviceThumbRY);
            }
            if (TriggerConfigs.Count > 0)
            {
                var processed = ProcessTriggerForPreview(DeviceLeftTrigger, TriggerConfigs[0]);
                TriggerConfigs[0].LiveValue = processed;
                TriggerConfigs[0].RawValue = (ushort)Math.Clamp((int)(processed * 65535), 0, 65535);
                UpdateTriggerCurveDot(TriggerConfigs[0], DeviceLeftTrigger);
            }
            if (TriggerConfigs.Count > 1)
            {
                var processed = ProcessTriggerForPreview(DeviceRightTrigger, TriggerConfigs[1]);
                TriggerConfigs[1].LiveValue = processed;
                TriggerConfigs[1].RawValue = (ushort)Math.Clamp((int)(processed * 65535), 0, 65535);
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
                DeadZoneShape shape)
        {
            // Convert to signed [-1, 1]
            double sx = (adjNormX - 0.5) * 2.0;
            double sy = (adjNormY - 0.5) * 2.0;
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
            // Sync stick config items from raw axes
            foreach (var stick in StickConfigs)
            {
                bool hasX = stick.AxisXIndex >= 0 && raw.Axes != null && stick.AxisXIndex < raw.Axes.Length;
                bool hasY = stick.AxisYIndex >= 0 && raw.Axes != null && stick.AxisYIndex < raw.Axes.Length;

                if (hasX) stick.HardwareRawX = raw.Axes[stick.AxisXIndex];
                if (hasY) stick.HardwareRawY = raw.Axes[stick.AxisYIndex];

                double normX = hasX ? (raw.Axes[stick.AxisXIndex] - (double)short.MinValue) / 65535.0 : 0.5;
                double normY = hasY ? (raw.Axes[stick.AxisYIndex] - (double)short.MinValue) / 65535.0 : 0.5;

                var (vx, ox, vy, oy) = ProcessStickForPreview(
                    normX + stick.CenterOffsetX / 200.0,
                    normY - stick.CenterOffsetY / 200.0,
                    stick.DeadZoneX, stick.DeadZoneY,
                    stick.AntiDeadZoneX, stick.AntiDeadZoneY,
                    stick.Linear, stick.MaxRangeX, stick.MaxRangeY,
                    stick.MaxRangeXNeg, stick.MaxRangeYNeg,
                    stick.SensitivityCurveX, stick.SensitivityCurveY,
                    stick.DeadZoneShape);

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
                    trig.RawValue = (ushort)Math.Clamp((int)(processed * 65535), 0, 65535);
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
