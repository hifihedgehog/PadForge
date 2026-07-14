using System;
using System.Collections.ObjectModel;
using System.Xml.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PadForge.ViewModels
{
    /// <summary>
    /// Per-slot PlayStation output configuration. Drives the
    /// <c>Adaptive Triggers</c> and <c>Lighting</c> tabs on PlayStation
    /// virtual controller slots. Lives parallel to
    /// <see cref="ExtendedSlotConfig"/> — same shape (ObservableObject,
    /// XML round-trip via a paired data record), different content.
    ///
    /// <para>The fields here are output-side: they drive Feature B
    /// (user-configured effect synthesis directly via
    /// <c>SDL_SendGamepadEffect</c>) and provide UI surfaces that
    /// Commit 3 will hook the audio-driven (#55) and macro-driven
    /// (#63) lightbar sources into. Game-driven Feature A passthrough
    /// is handled separately by the <c>DualSensePassthroughDispatcher</c>
    /// and doesn't read from this config.</para>
    /// </summary>
    public class DeviceSlotConfig : ObservableObject
    {
        public DeviceSlotConfig()
        {
            HookPalette(_lightbarPalette);
            HookInputReactivePalette(_lightbarInputReactivePalette);
        }

        // Subscribe collection + per-item PropertyChanged so the
        // dispatcher's OnConfigChanged catches any palette edit.
        // Without this, dragging an RGB slider on a palette entry would
        // not retrigger DispatchSnapshot — the entry's PropertyChanged
        // is internal to the entry and the parent collection wouldn't
        // see it.
        private void HookPalette(ObservableCollection<LightbarPaletteEntry> coll)
        {
            if (coll == null) return;
            coll.CollectionChanged += OnPaletteCollectionChanged;
            foreach (var entry in coll)
                if (entry != null) entry.PropertyChanged += OnPaletteEntryChanged;
        }

        private void UnhookPalette(ObservableCollection<LightbarPaletteEntry> coll)
        {
            if (coll == null) return;
            coll.CollectionChanged -= OnPaletteCollectionChanged;
            foreach (var entry in coll)
                if (entry != null) entry.PropertyChanged -= OnPaletteEntryChanged;
        }

        private void OnPaletteCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (LightbarPaletteEntry old in e.OldItems)
                    if (old != null) old.PropertyChanged -= OnPaletteEntryChanged;
            if (e.NewItems != null)
                foreach (LightbarPaletteEntry add in e.NewItems)
                    if (add != null) add.PropertyChanged += OnPaletteEntryChanged;
            OnPropertyChanged(nameof(LightbarPalette));
        }

        private void OnPaletteEntryChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(LightbarPalette));
        }

        // ────────────────────────────────────────────────
        //  Adaptive Triggers — per-trigger config
        // ────────────────────────────────────────────────

        private AdaptiveTriggerMode _leftTriggerMode = AdaptiveTriggerMode.Off;
        /// <summary>Left trigger effect mode. Default Off — the trigger
        /// reverts to its standard linear response when no game has
        /// driven a custom effect.</summary>
        public AdaptiveTriggerMode LeftTriggerMode
        {
            get => _leftTriggerMode;
            set => SetProperty(ref _leftTriggerMode, value);
        }

        private AdaptiveTriggerMode _rightTriggerMode = AdaptiveTriggerMode.Off;
        /// <summary>Right trigger effect mode. Default Off.</summary>
        public AdaptiveTriggerMode RightTriggerMode
        {
            get => _rightTriggerMode;
            set => SetProperty(ref _rightTriggerMode, value);
        }

        // Mode-shared parameters. Each mode reads the subset it needs;
        // others are ignored. The synthesizer in Commit 3 reads these
        // by mode and packs the 11-byte per-trigger payload accordingly.

        private byte _leftStartPosition;
        public byte LeftStartPosition
        {
            get => _leftStartPosition;
            set => SetProperty(ref _leftStartPosition, value);
        }

        private byte _leftEndPosition = 255;
        /// <summary>End of the trigger pull range that the active effect
        /// targets. Default 255 (full pull) so a fresh slot exposes the
        /// trigger's full travel; the reset command goes back to this.</summary>
        public byte LeftEndPosition
        {
            get => _leftEndPosition;
            set => SetProperty(ref _leftEndPosition, value);
        }

        private byte _leftStrength = 200;
        /// <summary>Trigger effect force, 0-255. Default 200 (substantial)
        /// so picking a non-Off mode produces immediate noticeable
        /// resistance without the user having to move the slider first.</summary>
        public byte LeftStrength
        {
            get => _leftStrength;
            set => SetProperty(ref _leftStrength, value);
        }

        private byte _leftFrequency = 10;
        /// <summary>Vibration frequency, 0-255 (low end of the range is
        /// where the firmware actually responds — dualsense-tester
        /// caps its UI at 15). Default 10 gives a moderate buzz
        /// frequency for Vibration / MultiplePositionVibration.</summary>
        public byte LeftFrequency
        {
            get => _leftFrequency;
            set => SetProperty(ref _leftFrequency, value);
        }

        private byte _rightStartPosition;
        public byte RightStartPosition
        {
            get => _rightStartPosition;
            set => SetProperty(ref _rightStartPosition, value);
        }

        private byte _rightEndPosition = 255;
        /// <summary>End of the trigger pull range that the active effect
        /// targets. Default 255 (full pull); see <see cref="LeftEndPosition"/>.</summary>
        public byte RightEndPosition
        {
            get => _rightEndPosition;
            set => SetProperty(ref _rightEndPosition, value);
        }

        private byte _rightStrength = 200;
        /// <summary>See <see cref="LeftStrength"/>.</summary>
        public byte RightStrength
        {
            get => _rightStrength;
            set => SetProperty(ref _rightStrength, value);
        }

        private byte _rightFrequency = 10;
        /// <summary>See <see cref="LeftFrequency"/>.</summary>
        public byte RightFrequency
        {
            get => _rightFrequency;
            set => SetProperty(ref _rightFrequency, value);
        }

        // ────────────────────────────────────────────────
        //  Lighting — solid base layer
        // ────────────────────────────────────────────────

        private byte _lightbarRed;
        public byte LightbarRed
        {
            get => _lightbarRed;
            set => SetProperty(ref _lightbarRed, value);
        }

        private byte _lightbarGreen;
        public byte LightbarGreen
        {
            get => _lightbarGreen;
            set => SetProperty(ref _lightbarGreen, value);
        }

        private byte _lightbarBlue = 0xFF;
        /// <summary>Lightbar blue channel. Default 0xFF — Sony's stock
        /// player-1 indicator color is solid blue, so a fresh slot lights
        /// the bar blue rather than dark when the user opens the tab.</summary>
        public byte LightbarBlue
        {
            get => _lightbarBlue;
            set => SetProperty(ref _lightbarBlue, value);
        }

        private bool _lightbarEnabled;
        /// <summary>Master toggle for the user-configured base lightbar
        /// color. Off (default) means the lightbar is whatever the game
        /// last wrote, or dark if no game is writing — matches the
        /// out-of-the-box DualSense experience. On means PadForge
        /// actively writes the configured RGB whenever no higher-priority
        /// source (game, macro, audio) is overwriting it.</summary>
        public bool LightbarEnabled
        {
            get => _lightbarEnabled;
            set => SetProperty(ref _lightbarEnabled, value);
        }

        private bool _audioPassthroughEnabled;
        /// <summary>Issue #83 — mirror the system default audio output to
        /// this pad's built-in speaker (USB Audio Class endpoint matched by
        /// Container ID, or the Bluetooth HID audio stream). Per assigned
        /// device, off by default. Macro sounds play through the speaker
        /// regardless of this toggle; this mirrors EVERYTHING the system
        /// default device plays, in parallel.</summary>
        public bool AudioPassthroughEnabled
        {
            get => _audioPassthroughEnabled;
            set => SetProperty(ref _audioPassthroughEnabled, value);
        }

        private string _audioMirrorSourceId = string.Empty;
        /// <summary>Which render endpoint the mirror captures (MMDevice ID).
        /// Empty = the system default device, re-resolved live. Lets games
        /// that output specific sounds to a separate playback device (e.g.
        /// Death Stranding's controller-speaker audio) target an endpoint
        /// that PadForge then forwards to the pad over USB or Bluetooth.</summary>
        public string AudioMirrorSourceId
        {
            get => _audioMirrorSourceId;
            set => SetProperty(ref _audioMirrorSourceId, value ?? string.Empty);
        }

        // ────────────────────────────────────────────────
        //  Haptic mirror engage gate (#185)
        // ────────────────────────────────────────────────
        // The haptic-tone mirror buzzes the pad with everything the system
        // plays, so it can gate on an input or on game rumble instead of
        // running always. Applies to haptic-tone sinks only (Joy-Con, Switch
        // Pro, Steam family); the Sony/Wii speaker mirrors play real audio
        // and stay ungated. Macro sounds are never gated.

        private string _audioMirrorEngageMode = "Always";
        /// <summary>When the haptic mirror plays: "Always" (default),
        /// "Input" (only while the chosen input is held), or "Rumble"
        /// (only while the slot's game-driven vibration is active).
        /// Same string-mode convention as GyroAimEngageMode.</summary>
        public string AudioMirrorEngageMode
        {
            get => _audioMirrorEngageMode;
            set => SetProperty(ref _audioMirrorEngageMode, string.IsNullOrEmpty(value) ? "Always" : value);
        }

        private string _audioMirrorEngageDeviceGuid = string.Empty;
        /// <summary>Device carrying the engage input for "Input" mode,
        /// mirroring GyroAimEngageDeviceGuid. Empty = no device chosen.</summary>
        public string AudioMirrorEngageDeviceGuid
        {
            get => _audioMirrorEngageDeviceGuid;
            set => SetProperty(ref _audioMirrorEngageDeviceGuid, value ?? string.Empty);
        }

        private string _audioMirrorEngageButton = string.Empty;
        /// <summary>Input descriptor held to engage the mirror in "Input"
        /// mode, mirroring GyroAimEngageButton. Empty in Input mode reads
        /// as always-on, matching the gyro-engage convention.</summary>
        public string AudioMirrorEngageButton
        {
            get => _audioMirrorEngageButton;
            set => SetProperty(ref _audioMirrorEngageButton, value ?? string.Empty);
        }

        private int _audioMirrorEngageReleaseMs = 500;
        /// <summary>How long the mirror keeps playing after the engage
        /// source drops (ms), so the tone does not clip off instantly.
        /// The reporter's suggested half-second is the default. Clamped
        /// 0..10000 at the consumer.</summary>
        public int AudioMirrorEngageReleaseMs
        {
            get => _audioMirrorEngageReleaseMs;
            set => SetProperty(ref _audioMirrorEngageReleaseMs, value);
        }

        // ────────────────────────────────────────────────
        //  High-tone filter (#202)
        // ────────────────────────────────────────────────
        // The haptic-tone sinks (Steam family, Joy-Con) reduce everything
        // they play — mirror audio, macro sounds, the Test button — to one
        // (pitch, amplitude) pair per tick. These two fields filter that
        // pair upstream of every family encoder, so both Steam Controller
        // generations, the Deck, and the Joy-Cons behave identically. The
        // Sony / Wii speaker mirrors play real audio, not tones, and are
        // unaffected.

        private string _audioToneFilterMode = "Off";
        /// <summary>What happens to detected pitches above
        /// <see cref="AudioToneLimitHz"/>: "Off" (default, everything
        /// plays), "Cut" (silenced), or "Fold" (octave-halved into the
        /// pass band, keeping the pitch class). Same string-mode
        /// convention as <see cref="AudioMirrorEngageMode"/>.</summary>
        public string AudioToneFilterMode
        {
            get => _audioToneFilterMode;
            set => SetProperty(ref _audioToneFilterMode, string.IsNullOrEmpty(value) ? "Off" : value);
        }

        private int _audioToneLimitHz = 800;
        /// <summary>Ceiling for Cut / Fold in Hz. The tone reducer detects
        /// 40-1300 Hz; the UI offers 100-1300 and the consumer clamps.
        /// 800 by default so engine and impact rumble keep their pitch
        /// while beeps above it are folded or cut.</summary>
        public int AudioToneLimitHz
        {
            get => _audioToneLimitHz;
            set => SetProperty(ref _audioToneLimitHz, value);
        }

        // ────────────────────────────────────────────────
        //  Lightbar: macro-driven override (#63)
        // ────────────────────────────────────────────────
        // Transient runtime state set by MacroActionType.LightbarColor
        // when a macro fires. Held until ExpiresAtUtc, then cleared
        // implicitly by HasActiveMacroLightbarOverride flipping false on
        // the next dispatch. Not persisted — overrides are tied to a
        // specific macro execution, not to the slot's saved config.
        //
        // Priority order in the synthesizer:
        //   1. Game-driven Feature A passthrough — packet-level, separate
        //      dispatcher (DualSensePassthroughDispatcher). Game writes win.
        //   2. Macro-driven override (these fields) — held while UtcNow
        //      < ExpiresAtUtc.
        //   3. AudioLightbarEnabled / animated-mode RGB — existing path.
        //   4. LightbarEnabled base color — existing path.
        //   5. Off — bytes left zero.

        private byte _macroOverrideR;
        [System.Xml.Serialization.XmlIgnore]
        public byte MacroOverrideR
        {
            get => _macroOverrideR;
            set => SetProperty(ref _macroOverrideR, value);
        }

        private byte _macroOverrideG;
        [System.Xml.Serialization.XmlIgnore]
        public byte MacroOverrideG
        {
            get => _macroOverrideG;
            set => SetProperty(ref _macroOverrideG, value);
        }

        private byte _macroOverrideB;
        [System.Xml.Serialization.XmlIgnore]
        public byte MacroOverrideB
        {
            get => _macroOverrideB;
            set => SetProperty(ref _macroOverrideB, value);
        }

        private DateTime _macroOverrideStartUtc = DateTime.MinValue;
        /// <summary>Fire timestamp for the active override. Boundary
        /// for the hold window when <see cref="MacroOverrideHoldEndUtc"/>
        /// > Start.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public DateTime MacroOverrideStartUtc
        {
            get => _macroOverrideStartUtc;
            set => SetProperty(ref _macroOverrideStartUtc, value);
        }

        private DateTime _macroOverrideHoldEndUtc = DateTime.MinValue;
        /// <summary>Hold-end timestamp. The override stays at full
        /// intensity over [Start, HoldEnd], then fades linearly from 1.0
        /// to 0.0 over [HoldEnd, Expires]. HoldEnd == Start means start
        /// fading immediately; HoldEnd == Expires means cut directly to
        /// off after the hold (no fade).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public DateTime MacroOverrideHoldEndUtc
        {
            get => _macroOverrideHoldEndUtc;
            set => SetProperty(ref _macroOverrideHoldEndUtc, value);
        }

        private DateTime _macroOverrideExpiresAtUtc = DateTime.MinValue;
        [System.Xml.Serialization.XmlIgnore]
        public DateTime MacroOverrideExpiresAtUtc
        {
            get => _macroOverrideExpiresAtUtc;
            set => SetProperty(ref _macroOverrideExpiresAtUtc, value);
        }

        private MacroLightbarHoldMode _macroOverrideHoldMode = MacroLightbarHoldMode.Reactive;
        /// <summary>Reactive (decay-fade) or Sticky (held until cleared).
        /// Reactive computes intensity over the [Start, Expires] window;
        /// Sticky returns full intensity until <see cref="ClearMacroOverride"/>
        /// runs.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public MacroLightbarHoldMode MacroOverrideHoldMode
        {
            get => _macroOverrideHoldMode;
            set => SetProperty(ref _macroOverrideHoldMode, value);
        }

        /// <summary>True while the macro-driven override is still within
        /// its hold window. Read by the synthesizer and the animation
        /// timer's stop-condition. Cleared implicitly by the timestamp
        /// rolling past for Reactive holds; explicitly via
        /// <see cref="ClearMacroOverride"/> for Sticky holds.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool HasActiveMacroLightbarOverride
            => DateTime.UtcNow < _macroOverrideExpiresAtUtc;

        /// <summary>0..1 intensity scalar for the override RGB. 1.0 for
        /// Sticky holds; for Reactive holds, 1.0 across [Start, HoldEnd]
        /// then a linear ramp from 1.0 to 0.0 across [HoldEnd, Expires].
        /// The synthesizer multiplies the override RGB by this so a
        /// Reactive flash fades smoothly the same way the InputReactive
        /// lightbar mode does. Returns 0 when no override is active.</summary>
        public float ComputeMacroOverrideIntensity()
        {
            if (!HasActiveMacroLightbarOverride) return 0f;
            if (_macroOverrideHoldMode == MacroLightbarHoldMode.Sticky) return 1f;
            DateTime now = DateTime.UtcNow;
            if (now <= _macroOverrideHoldEndUtc) return 1f;
            double fade = (_macroOverrideExpiresAtUtc - _macroOverrideHoldEndUtc).TotalMilliseconds;
            if (fade <= 0) return 0f;
            double fadeElapsed = (now - _macroOverrideHoldEndUtc).TotalMilliseconds;
            return (float)Math.Clamp(1.0 - fadeElapsed / fade, 0.0, 1.0);
        }

        /// <summary>Releases any active override (Sticky or Reactive) so
        /// the synthesizer falls back to the configured Lighting tab
        /// state on the next dispatch. Drives the
        /// <see cref="MacroActionType.LightbarColorClear"/> action.</summary>
        public void ClearMacroOverride()
        {
            if (_macroOverrideExpiresAtUtc != DateTime.MinValue)
                MacroOverrideExpiresAtUtc = DateTime.MinValue;
            if (_macroOverrideHoldEndUtc != DateTime.MinValue)
                MacroOverrideHoldEndUtc = DateTime.MinValue;
        }

        // ────────────────────────────────────────────────
        //  Mic LED mode (DualSense only) — mute LED state on the front edge
        // ────────────────────────────────────────────────

        private MicLedMode _micLedMode;
        /// <summary>Mic mute LED state. The DS5 firmware exposes three
        /// modes at byte 8 (muteLedControl): Off, Solid, Pulse. There's
        /// no separate brightness — these are the firmware-supported
        /// states.</summary>
        public MicLedMode MicLedMode
        {
            get => _micLedMode;
            set
            {
                if (SetProperty(ref _micLedMode, value))
                    OnPropertyChanged(nameof(IsMicLedFollowDevice));
            }
        }

        private string _micLedFollowDeviceId = string.Empty;
        /// <summary>CoreAudio endpoint id (the same string returned by
        /// MMDevice.ID) the FollowDeviceMute mode polls for mute state.
        /// Empty string means "no device picked yet" — synthesizer falls
        /// back to Off in that case. Persisted as a plain string so
        /// settings round-trip survives endpoint reconnects (Windows
        /// keeps the same id for a given physical device across
        /// unplug / replug cycles).</summary>
        public string MicLedFollowDeviceId
        {
            get => _micLedFollowDeviceId;
            set => SetProperty(ref _micLedFollowDeviceId, value ?? string.Empty);
        }

        /// <summary>Bound to the Mic LED follow-device dropdown's
        /// visibility — only show the picker when the user has picked
        /// FollowDeviceMute as the mic-LED mode.</summary>
        public bool IsMicLedFollowDevice => _micLedMode == MicLedMode.FollowDeviceMute;

        // Backwards-compat shim. Old XML uses bool MicLightOn; we keep
        // the property for round-tripping but route it through MicLedMode.
        // True maps to Solid; False maps to Off. Pulse is opt-in via the
        // new MicLedMode property.
        public bool MicLightOn
        {
            get => _micLedMode != MicLedMode.Off;
            set
            {
                var target = value ? MicLedMode.Solid : MicLedMode.Off;
                if (_micLedMode != target)
                    MicLedMode = target;
            }
        }

        private System.Collections.Generic.List<MicLedDeviceItem> _micLedAvailableDevices;
        /// <summary>List of audio endpoints the mic-LED FollowDeviceMute
        /// dropdown picks from. Lazily populated on first access; the
        /// PadPage's DropDownOpened handler calls
        /// <see cref="RefreshMicLedDevices"/> so the user always sees the
        /// current device list when they open the picker (covers
        /// post-launch unplug / replug).</summary>
        public System.Collections.Generic.List<MicLedDeviceItem> MicLedAvailableDevices
        {
            get
            {
                _micLedAvailableDevices ??= BuildMicLedDeviceList();
                return _micLedAvailableDevices;
            }
        }

        public void RefreshMicLedDevices()
        {
            _micLedAvailableDevices = BuildMicLedDeviceList();
            OnPropertyChanged(nameof(MicLedAvailableDevices));
        }

        private static System.Collections.Generic.List<MicLedDeviceItem> BuildMicLedDeviceList()
        {
            var list = new System.Collections.Generic.List<MicLedDeviceItem>();
            try
            {
                var endpoints = PadForge.Common.Input.AudioMuteService.EnumerateEndpoints();
                foreach (var ep in endpoints)
                {
                    string tag = ep.IsInput ? "[In]" : "[Out]";
                    list.Add(new MicLedDeviceItem(ep.Id, $"{tag} {ep.FriendlyName}"));
                }
            }
            catch
            {
                // Audio stack unavailable — combo just shows empty.
            }
            return list;
        }

        private PlayerLedMode _playerLedMode = PlayerLedMode.PlayerNumber;
        /// <summary>Bottom-row player indicator LEDs (1-5 small white
        /// LEDs below the touchpad). PlayerNumber (default, #191) = the
        /// pattern for the virtual controller's number; Off = all dark
        /// (the stealth choice); PlayerN = a fixed pattern; All = every
        /// LED lit.
        /// Bit pattern at byte 43 per dualsense-tester:
        /// Off=0x00, P1=0x04, P2=0x0A, P3=0x15, P4=0x1B, All=0x1F.</summary>
        public PlayerLedMode PlayerLedMode
        {
            get => _playerLedMode;
            set => SetProperty(ref _playerLedMode, value);
        }

        private PlayerLedBrightness _playerLedBrightness = PlayerLedBrightness.High;
        /// <summary>Brightness of the player indicator LEDs at byte 42.
        /// Firmware values: 0=High, 1=Medium, 2=Low. Doesn't affect
        /// the lightbar (lightbar brightness is implicit in RGB).</summary>
        public PlayerLedBrightness PlayerLedBrightness
        {
            get => _playerLedBrightness;
            set => SetProperty(ref _playerLedBrightness, value);
        }

        private GuideLedMode _guideLedMode = GuideLedMode.DeviceDefault;
        /// <summary>Guide/Home button LED handling (discussion #209,
        /// Switch home LED #226). DeviceDefault never writes, leaving the
        /// firmware's own brightness untouched. Fixed holds
        /// <see cref="GuideLedBrightness"/>. Battery re-maps the device's
        /// battery percent to brightness on a slow cadence, floored at 10
        /// so a low battery stays visible. Xbox One and later pads take
        /// the GIP LED command over the \\.\XboxGIP interface (USB only,
        /// XboxGipGuideLedWriter). The 2015 Steam Controller takes SDL's
        /// process-global home-LED hint (SteamHomeLedSetter). Switch Pro
        /// Controllers, right Joy-Cons, the combined pair, and the
        /// charging grip take per-device SDL_SetJoystickLED
        /// (SwitchHomeLedSetter).</summary>
        public GuideLedMode GuideLedMode
        {
            get => _guideLedMode;
            set
            {
                if (SetProperty(ref _guideLedMode, value))
                    OnPropertyChanged(nameof(IsGuideLedFixed));
            }
        }

        /// <summary>True when <see cref="GuideLedMode"/> is Fixed. Gates
        /// the brightness slider row on the Lighting tab.</summary>
        [XmlIgnore]
        public bool IsGuideLedFixed => _guideLedMode == GuideLedMode.Fixed;

        private int _guideLedBrightness = 100;
        /// <summary>Fixed-mode Guide LED brightness percent, 0 (off) to
        /// 100 (full). The writers scale it onto each device's own range
        /// (0-47 for GIP per MS-GIPUSB, 0..1 for the SDL hint, a 4-bit
        /// subcommand 0x38 intensity for the Switch home LED).</summary>
        public int GuideLedBrightness
        {
            get => _guideLedBrightness;
            set => SetProperty(ref _guideLedBrightness, Math.Clamp(value, 0, 100));
        }

        // ────────────────────────────────────────────────
        //  Lightbar — unified mode picker. Replaces the old separate
        //  LightbarEnabled and AudioLightbarEnabled toggles. The legacy
        //  bools still exist below for XML round-trip and migration on
        //  load (SettingsService.ApplyDeviceSlotConfigs maps them into
        //  LightbarMode if LightbarMode is at its default).
        // ────────────────────────────────────────────────

        private LightbarMode _lightbarMode = LightbarMode.PlayerNumber;
        /// <summary>Active lightbar BASE effect. PlayerNumber (default,
        /// #191) idles on the Sony player color for the virtual
        /// controller's number and lets a game's write persist for the
        /// session. Off is a deliberate hard-off that paints black every
        /// dispatch (the stealth choice). Animated modes (Breathing /
        /// Rainbow / ColorCycle / Audio*) run on the dispatcher's
        /// periodic timer. The InputReactive* values still live in the
        /// enum for XML round-trip but are migrated on load to
        /// <see cref="InputReactiveMode"/>; see SettingsService's
        /// ApplyDeviceSlotConfigData.</summary>
        public LightbarMode LightbarMode
        {
            get => _lightbarMode;
            set
            {
                if (SetProperty(ref _lightbarMode, value))
                {
                    OnPropertyChanged(nameof(ShowPaletteForBase));
                    OnPropertyChanged(nameof(ShowPaletteForOverlay));
                }
            }
        }

        private InputReactiveMode _inputReactiveMode = InputReactiveMode.Off;
        /// <summary>Active input-reactive OVERLAY effect. Layered on
        /// top of <see cref="LightbarMode"/> — when a button press
        /// pulses, this overlay's color blends over the base color by
        /// the pulse intensity (full override at intensity 1.0, fades
        /// back to base as intensity decays to 0). Macro lightbar
        /// overrides still beat both. Off means no overlay.</summary>
        public InputReactiveMode InputReactiveMode
        {
            get => _inputReactiveMode;
            set
            {
                if (SetProperty(ref _inputReactiveMode, value))
                {
                    OnPropertyChanged(nameof(IsInputReactiveActive));
                    OnPropertyChanged(nameof(ShowPaletteForBase));
                    OnPropertyChanged(nameof(ShowPaletteForOverlay));
                    OnPropertyChanged(nameof(IsInputReactiveFixed));
                }
            }
        }

        /// <summary>True when the input-reactive overlay is enabled
        /// (any non-Off variant). UI binds Hold / Decay slider
        /// visibility to this.</summary>
        public bool IsInputReactiveActive => _inputReactiveMode != InputReactiveMode.Off;

        /// <summary>True when the palette editor is owned by the
        /// LightbarMode = ColorCycle base mode. Drives the palette
        /// instance that lives under the LightbarMode dropdown so its
        /// settings sit directly below the dropdown that revealed them.</summary>
        public bool ShowPaletteForBase =>
            _lightbarMode == LightbarMode.ColorCycle;

        /// <summary>True when the palette editor is owned by the
        /// InputReactive = Cycle overlay AND ColorCycle isn't the base
        /// (so the palette never renders twice). Drives the palette
        /// instance that lives under the InputReactive dropdown so a
        /// user picking Cycle there gets the editor next to the dropdown
        /// they just used.</summary>
        public bool ShowPaletteForOverlay =>
            _inputReactiveMode == InputReactiveMode.Cycle;

        /// <summary>True when the input-reactive overlay is the
        /// Fixed variant — the color picker for the per-press flash
        /// color shows in this case (separate from the base
        /// LightbarRed/Green/Blue used by Static / Breathing).</summary>
        public bool IsInputReactiveFixed =>
            _inputReactiveMode == InputReactiveMode.Fixed;

        // Per-press flash color used by InputReactiveMode.Fixed. Kept
        // separate from LightbarRed/Green/Blue so users can layer
        // (e.g. Static blue base + white reactive flash on press).
        // Defaults to white so a new Fixed selection produces a
        // visible flash without the user immediately reaching for
        // the picker.
        private byte _inputReactiveR = 0xFF;
        public byte InputReactiveR
        {
            get => _inputReactiveR;
            set => SetProperty(ref _inputReactiveR, value);
        }

        private byte _inputReactiveG = 0xFF;
        public byte InputReactiveG
        {
            get => _inputReactiveG;
            set => SetProperty(ref _inputReactiveG, value);
        }

        private byte _inputReactiveB = 0xFF;
        public byte InputReactiveB
        {
            get => _inputReactiveB;
            set => SetProperty(ref _inputReactiveB, value);
        }

        private int _lightbarPeriodMs = 3000;
        /// <summary>Animation period in milliseconds for time-based modes:
        /// one full Breathing fade-in/out cycle, one full Rainbow hue
        /// rotation, one full ColorCycle palette traversal, and the hue
        /// rotation speed for AudioPulseRainbow. Clamped 250..10000.</summary>
        public int LightbarPeriodMs
        {
            get => _lightbarPeriodMs;
            set => SetProperty(ref _lightbarPeriodMs, Math.Clamp(value, 250, 10000));
        }

        private bool _lightbarColorCycleSmooth = true;
        /// <summary>ColorCycle interpolation: true blends linearly between
        /// adjacent palette entries, false hops instantly at each step.</summary>
        public bool LightbarColorCycleSmooth
        {
            get => _lightbarColorCycleSmooth;
            set => SetProperty(ref _lightbarColorCycleSmooth, value);
        }

        private int _lightbarRainbowBrightness = 100;
        /// <summary>Rainbow mode brightness, 0..100. The base Rainbow effect
        /// runs at full HSV value (V=1.0) which the firmware renders as
        /// peak brightness — fine for showcasing the lightbar but visually
        /// loud at night / desk distance. This scales the final RGB output
        /// linearly so the user can dim Rainbow without affecting other
        /// modes' colors. Static / Breathing / AudioPulse already let the
        /// user pick a darker base RGB; Rainbow has hardcoded saturation
        /// and hue progression so it's the one mode that needs an explicit
        /// brightness control.</summary>
        public int LightbarRainbowBrightness
        {
            get => _lightbarRainbowBrightness;
            set => SetProperty(ref _lightbarRainbowBrightness, Math.Clamp(value, 0, 100));
        }

        // Battery-mode endpoint colors. Defaults to red @ 0% → green @ 100%
        // matching the canonical "low fuel = red, full = green" convention.
        // The synthesizer linearly interpolates between Low and High using
        // the current per-device battery percent.
        private byte _lightbarBatteryLowR  = 0xFF;
        public byte LightbarBatteryLowR  { get => _lightbarBatteryLowR;  set => SetProperty(ref _lightbarBatteryLowR,  value); }
        private byte _lightbarBatteryLowG;
        public byte LightbarBatteryLowG  { get => _lightbarBatteryLowG;  set => SetProperty(ref _lightbarBatteryLowG,  value); }
        private byte _lightbarBatteryLowB;
        public byte LightbarBatteryLowB  { get => _lightbarBatteryLowB;  set => SetProperty(ref _lightbarBatteryLowB,  value); }

        private byte _lightbarBatteryHighR;
        public byte LightbarBatteryHighR { get => _lightbarBatteryHighR; set => SetProperty(ref _lightbarBatteryHighR, value); }
        private byte _lightbarBatteryHighG = 0xFF;
        public byte LightbarBatteryHighG { get => _lightbarBatteryHighG; set => SetProperty(ref _lightbarBatteryHighG, value); }
        private byte _lightbarBatteryHighB;
        public byte LightbarBatteryHighB { get => _lightbarBatteryHighB; set => SetProperty(ref _lightbarBatteryHighB, value); }

        // Variable-length palette shared by ColorCycle and InputReactive
        // modes. Defaults to four primaries (red, green, blue, yellow);
        // user can add or remove entries from the Lighting tab. Synth
        // iterates with idx % Count so any size from 1..N works.
        //
        // Threading: the collection is mutated on the UI thread (palette
        // commands) and read on the lighting timer thread (DrainInputPulses
        // and Ds5EffectSynthesizer.PaletteAt). ObservableCollection<T> is
        // not thread-safe, so timer-thread reads MUST go through
        // SnapshotLightbarPalette() — calling .Count or indexing the live
        // collection from off-thread can throw or read torn state during
        // a concurrent UI add/remove.
        private readonly object _lightbarPaletteLock = new();
        private ObservableCollection<LightbarPaletteEntry> _lightbarPalette
            = new ObservableCollection<LightbarPaletteEntry>
            {
                new LightbarPaletteEntry(0xFF, 0x00, 0x00),
                new LightbarPaletteEntry(0x00, 0xFF, 0x00),
                new LightbarPaletteEntry(0x00, 0x00, 0xFF),
                new LightbarPaletteEntry(0xFF, 0xFF, 0x00),
            };
        public ObservableCollection<LightbarPaletteEntry> LightbarPalette
        {
            get => _lightbarPalette;
            set
            {
                var v = value ?? new ObservableCollection<LightbarPaletteEntry>();
                if (_lightbarPalette == v) return;
                lock (_lightbarPaletteLock)
                {
                    UnhookPalette(_lightbarPalette);
                    _lightbarPalette = v;
                    HookPalette(_lightbarPalette);
                }
                OnPropertyChanged(nameof(LightbarPalette));
            }
        }

        /// <summary>Thread-safe snapshot of the current palette colors.
        /// Timer-thread consumers call this instead of touching
        /// <see cref="LightbarPalette"/> directly so a concurrent UI-thread
        /// Add / Remove / Clear can't tear the read.</summary>
        public LightbarPaletteEntry[] SnapshotLightbarPalette()
        {
            lock (_lightbarPaletteLock)
            {
                return _lightbarPalette.ToArray();
            }
        }

        /// <summary>Atomically replace the palette contents with a new set
        /// of entries under the same lock the timer-thread snapshot uses.
        /// Settings load drives this when restoring a saved config.</summary>
        public void ReplaceLightbarPalette(IEnumerable<LightbarPaletteEntry> entries)
        {
            lock (_lightbarPaletteLock)
            {
                _lightbarPalette.Clear();
                if (entries != null)
                {
                    foreach (var e in entries)
                        _lightbarPalette.Add(e);
                }
            }
        }

        public RelayCommand AddPaletteColorCommand =>
            _addPalette ??= new RelayCommand(() =>
            {
                // Roll a fresh hue distinct from the last entry. Keeps the
                // newly added swatch visually different from the one above
                // so the user can immediately see it landed.
                byte r = 0xFF, g = 0xFF, b = 0xFF;
                lock (_lightbarPaletteLock)
                {
                    if (_lightbarPalette.Count > 0)
                    {
                        var last = _lightbarPalette[_lightbarPalette.Count - 1];
                        if (last.R == 0xFF && last.G == 0x00 && last.B == 0x00) { r = 0x00; g = 0xFF; b = 0x00; }
                        else if (last.R == 0x00 && last.G == 0xFF && last.B == 0x00) { r = 0x00; g = 0x00; b = 0xFF; }
                        else if (last.R == 0x00 && last.G == 0x00 && last.B == 0xFF) { r = 0xFF; g = 0xFF; b = 0x00; }
                        else { r = 0xFF; g = 0x00; b = 0x00; }
                    }
                    _lightbarPalette.Add(new LightbarPaletteEntry(r, g, b));
                }
            });
        private RelayCommand _addPalette;

        public RelayCommand<LightbarPaletteEntry> RemovePaletteColorCommand =>
            _removePalette ??= new RelayCommand<LightbarPaletteEntry>(entry =>
            {
                if (entry == null) return;
                lock (_lightbarPaletteLock)
                {
                    if (_lightbarPalette.Count <= 1) return; // never let it go empty
                    _lightbarPalette.Remove(entry);
                }
            });
        private RelayCommand<LightbarPaletteEntry> _removePalette;

        // ── InputReactive = Cycle palette — SEPARATE from the ColorCycle palette above. ──
        // The base ColorCycle effect and the InputReactive Cycle overlay each step their own
        // palette. They were briefly wired to the one collection above, so editing one changed
        // the other; that was never intended. Same threading contract: timer-thread reads go
        // through SnapshotLightbarInputReactivePalette().
        private readonly object _lightbarInputReactivePaletteLock = new();
        private ObservableCollection<LightbarPaletteEntry> _lightbarInputReactivePalette
            = new ObservableCollection<LightbarPaletteEntry>
            {
                new LightbarPaletteEntry(0xFF, 0x00, 0x00),
                new LightbarPaletteEntry(0x00, 0xFF, 0x00),
                new LightbarPaletteEntry(0x00, 0x00, 0xFF),
                new LightbarPaletteEntry(0xFF, 0xFF, 0x00),
            };
        public ObservableCollection<LightbarPaletteEntry> LightbarInputReactivePalette
        {
            get => _lightbarInputReactivePalette;
            set
            {
                var v = value ?? new ObservableCollection<LightbarPaletteEntry>();
                if (_lightbarInputReactivePalette == v) return;
                lock (_lightbarInputReactivePaletteLock)
                {
                    UnhookInputReactivePalette(_lightbarInputReactivePalette);
                    _lightbarInputReactivePalette = v;
                    HookInputReactivePalette(_lightbarInputReactivePalette);
                }
                OnPropertyChanged(nameof(LightbarInputReactivePalette));
            }
        }

        public LightbarPaletteEntry[] SnapshotLightbarInputReactivePalette()
        {
            lock (_lightbarInputReactivePaletteLock)
                return _lightbarInputReactivePalette.ToArray();
        }

        public void ReplaceLightbarInputReactivePalette(IEnumerable<LightbarPaletteEntry> entries)
        {
            lock (_lightbarInputReactivePaletteLock)
            {
                _lightbarInputReactivePalette.Clear();
                if (entries != null)
                    foreach (var e in entries) _lightbarInputReactivePalette.Add(e);
            }
        }

        // Subscribed entries tracked explicitly because Clear() raises
        // Reset with no OldItems, which would otherwise skip the per-entry
        // unsubscribes (same idiom as PadViewModel's _directCountHooked).
        private readonly List<LightbarPaletteEntry> _paletteEntriesHooked = new();

        private void HookInputReactivePalette(ObservableCollection<LightbarPaletteEntry> coll)
        {
            if (coll == null) return;
            coll.CollectionChanged += OnInputReactivePaletteCollectionChanged;
            foreach (var entry in coll)
                if (entry != null)
                {
                    entry.PropertyChanged += OnInputReactivePaletteEntryChanged;
                    _paletteEntriesHooked.Add(entry);
                }
        }
        private void UnhookInputReactivePalette(ObservableCollection<LightbarPaletteEntry> coll)
        {
            if (coll == null) return;
            coll.CollectionChanged -= OnInputReactivePaletteCollectionChanged;
            foreach (var hooked in _paletteEntriesHooked)
                hooked.PropertyChanged -= OnInputReactivePaletteEntryChanged;
            _paletteEntriesHooked.Clear();
        }
        private void OnInputReactivePaletteCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                foreach (var hooked in _paletteEntriesHooked)
                    hooked.PropertyChanged -= OnInputReactivePaletteEntryChanged;
                _paletteEntriesHooked.Clear();
                if (sender is ObservableCollection<LightbarPaletteEntry> coll)
                    foreach (var entry in coll)
                        if (entry != null)
                        {
                            entry.PropertyChanged += OnInputReactivePaletteEntryChanged;
                            _paletteEntriesHooked.Add(entry);
                        }
            }
            else
            {
                if (e.OldItems != null)
                    foreach (LightbarPaletteEntry old in e.OldItems)
                        if (old != null)
                        {
                            old.PropertyChanged -= OnInputReactivePaletteEntryChanged;
                            _paletteEntriesHooked.Remove(old);
                        }
                if (e.NewItems != null)
                    foreach (LightbarPaletteEntry add in e.NewItems)
                        if (add != null)
                        {
                            add.PropertyChanged += OnInputReactivePaletteEntryChanged;
                            _paletteEntriesHooked.Add(add);
                        }
            }
            OnPropertyChanged(nameof(LightbarInputReactivePalette));
        }
        private void OnInputReactivePaletteEntryChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
            => OnPropertyChanged(nameof(LightbarInputReactivePalette));

        public RelayCommand AddInputReactivePaletteColorCommand =>
            _addInputReactivePalette ??= new RelayCommand(() =>
            {
                byte r = 0xFF, g = 0xFF, b = 0xFF;
                lock (_lightbarInputReactivePaletteLock)
                {
                    if (_lightbarInputReactivePalette.Count > 0)
                    {
                        var last = _lightbarInputReactivePalette[_lightbarInputReactivePalette.Count - 1];
                        if (last.R == 0xFF && last.G == 0x00 && last.B == 0x00) { r = 0x00; g = 0xFF; b = 0x00; }
                        else if (last.R == 0x00 && last.G == 0xFF && last.B == 0x00) { r = 0x00; g = 0x00; b = 0xFF; }
                        else if (last.R == 0x00 && last.G == 0x00 && last.B == 0xFF) { r = 0xFF; g = 0xFF; b = 0x00; }
                        else { r = 0xFF; g = 0x00; b = 0x00; }
                    }
                    _lightbarInputReactivePalette.Add(new LightbarPaletteEntry(r, g, b));
                }
            });
        private RelayCommand _addInputReactivePalette;

        public RelayCommand<LightbarPaletteEntry> RemoveInputReactivePaletteColorCommand =>
            _removeInputReactivePalette ??= new RelayCommand<LightbarPaletteEntry>(entry =>
            {
                if (entry == null) return;
                lock (_lightbarInputReactivePaletteLock)
                {
                    if (_lightbarInputReactivePalette.Count <= 1) return; // never let it go empty
                    _lightbarInputReactivePalette.Remove(entry);
                }
            });
        private RelayCommand<LightbarPaletteEntry> _removeInputReactivePalette;

        public RelayCommand ResetInputReactivePaletteCommand =>
            _resetInputReactivePalette ??= new RelayCommand(() =>
            {
                lock (_lightbarInputReactivePaletteLock)
                {
                    _lightbarInputReactivePalette.Clear();
                    _lightbarInputReactivePalette.Add(new LightbarPaletteEntry(0xFF, 0x00, 0x00));
                    _lightbarInputReactivePalette.Add(new LightbarPaletteEntry(0x00, 0xFF, 0x00));
                    _lightbarInputReactivePalette.Add(new LightbarPaletteEntry(0x00, 0x00, 0xFF));
                    _lightbarInputReactivePalette.Add(new LightbarPaletteEntry(0xFF, 0xFF, 0x00));
                }
            });
        private RelayCommand _resetInputReactivePalette;

        private int _lightbarInputHoldMs;
        /// <summary>Hold time for InputReactive pulses, in milliseconds.
        /// A button press flashes the chosen color at full intensity for
        /// this long before the fade starts. Set to 0 (default) for an
        /// immediate fade out — matches the v3.1.0 behavior.</summary>
        public int LightbarInputHoldMs
        {
            get => _lightbarInputHoldMs;
            set => SetProperty(ref _lightbarInputHoldMs, Math.Clamp(value, 0, 5000));
        }

        private int _lightbarInputDecayMs = 600;
        /// <summary>Fade-out duration for InputReactive pulses (after the
        /// hold period elapses), in milliseconds. Set to 0 for a hard
        /// cutoff — useful with a non-zero <see cref="LightbarInputHoldMs"/>
        /// to produce a clean on/off blink. The pre-v3.1.1 single-decay
        /// behaviour is recovered by Hold=0, Decay=600 (the default).</summary>
        public int LightbarInputDecayMs
        {
            get => _lightbarInputDecayMs;
            set => SetProperty(ref _lightbarInputDecayMs, Math.Clamp(value, 0, 5000));
        }

        // ────────────────────────────────────────────────
        //  Master enable for Feature B (user-configured effects)
        // ────────────────────────────────────────────────

        // ────────────────────────────────────────────────
        //  Audio-to-lightbar (DSY-style) — modulates the user's
        //  configured lightbar color by the system audio peak. Taps
        //  AudioBassDetector pre-filter so the lightbar follows the
        //  full audio spectrum, independent of the bass-cutoff setting
        //  the audio-rumble feature uses.
        // ────────────────────────────────────────────────

        private bool _audioLightbarEnabled;
        /// <summary>When true, the lightbar RGB is multiplied by the
        /// system audio peak each tick — pulsing the user's chosen
        /// color with whatever is playing through the default render
        /// device. When the user has both <c>LightbarEnabled</c> and
        /// this on, this wins; the static color is the "max" point of
        /// the modulation.</summary>
        public bool AudioLightbarEnabled
        {
            get => _audioLightbarEnabled;
            set => SetProperty(ref _audioLightbarEnabled, value);
        }

        private double _audioLightbarSensitivity = 4.0;
        /// <summary>Pre-clamp gain applied to the audio peak before it
        /// modulates the lightbar. Same range/default as the audio-rumble
        /// sensitivity so the two controls feel consistent.</summary>
        public double AudioLightbarSensitivity
        {
            get => _audioLightbarSensitivity;
            set => SetProperty(ref _audioLightbarSensitivity, Math.Clamp(value, 1.0, 20.0));
        }

        private AudioLightbarMode _audioLightbarMode = AudioLightbarMode.Pulse;
        /// <summary>Which audio-to-lightbar behavior to use.
        /// <para>Pulse — DSY-style: multiply the user's static base
        /// color by the audio peak each tick. Black at silence, full
        /// color at peak.</para>
        /// <para>Thresholds — issue #55 primary request: pick from
        /// three colors based on which audio band the peak falls into
        /// (quiet / medium / loud). Use case is FPS games where the
        /// lightbar shifts green→yellow→red as ambient noise rises.</para>
        /// </summary>
        public AudioLightbarMode AudioLightbarMode
        {
            get => _audioLightbarMode;
            set => SetProperty(ref _audioLightbarMode, value);
        }

        // Threshold-mode color triplets. Defaults map the FPS use case
        // from the issue: green when quiet, yellow when audio rises,
        // red on loud transients.
        private byte _audioLowR;
        public byte AudioLowR { get => _audioLowR; set => SetProperty(ref _audioLowR, value); }
        private byte _audioLowG = 0xFF;
        public byte AudioLowG { get => _audioLowG; set => SetProperty(ref _audioLowG, value); }
        private byte _audioLowB;
        public byte AudioLowB { get => _audioLowB; set => SetProperty(ref _audioLowB, value); }

        private byte _audioMidR = 0xFF;
        public byte AudioMidR { get => _audioMidR; set => SetProperty(ref _audioMidR, value); }
        private byte _audioMidG = 0xFF;
        public byte AudioMidG { get => _audioMidG; set => SetProperty(ref _audioMidG, value); }
        private byte _audioMidB;
        public byte AudioMidB { get => _audioMidB; set => SetProperty(ref _audioMidB, value); }

        private byte _audioHighR = 0xFF;
        public byte AudioHighR { get => _audioHighR; set => SetProperty(ref _audioHighR, value); }
        private byte _audioHighG;
        public byte AudioHighG { get => _audioHighG; set => SetProperty(ref _audioHighG, value); }
        private byte _audioHighB;
        public byte AudioHighB { get => _audioHighB; set => SetProperty(ref _audioHighB, value); }

        private double _audioLowToMidPercent = 33;
        /// <summary>Audio peak (post-sensitivity) percentage at which
        /// the lightbar transitions from the Low color to the Mid color.
        /// 0..100, default 33 — matches a roughly even split into
        /// thirds against the Mid→High threshold's default of 66.</summary>
        public double AudioLowToMidPercent
        {
            get => _audioLowToMidPercent;
            set => SetProperty(ref _audioLowToMidPercent, Math.Clamp(value, 0, 100));
        }

        private double _audioMidToHighPercent = 66;
        /// <summary>Audio peak (post-sensitivity) percentage at which
        /// the lightbar transitions from the Mid color to the High
        /// color. 0..100, default 66.</summary>
        public double AudioMidToHighPercent
        {
            get => _audioMidToHighPercent;
            set => SetProperty(ref _audioMidToHighPercent, Math.Clamp(value, 0, 100));
        }

        private double _audioCrossFadePercent = 5.0;
        /// <summary>Half-width of the crossfade window (in audio peak
        /// percentage) around each threshold boundary in CrossFade mode.
        /// 0..50, default 5. At 5, a peak within ±5% of a threshold is
        /// blended between the adjacent colors; outside that window the
        /// behavior matches the discrete Thresholds mode. Above 0,
        /// peak% < threshold% - this stays the prior color; peak% >
        /// threshold% + this stays the next color.</summary>
        public double AudioCrossFadePercent
        {
            get => _audioCrossFadePercent;
            set => SetProperty(ref _audioCrossFadePercent, Math.Clamp(value, 0, 50));
        }

        // ────────────────────────────────────────────────
        //  Reset commands (per-control)
        //  Mirror the per-row reset pattern on the Sticks / Triggers tabs.
        //  Each command resets one logical control to its safe default;
        //  every PropertyChanged that fires from a Reset feeds through
        //  UserEffectsDispatcher and immediately re-syncs the physical
        //  pad.
        // ────────────────────────────────────────────────

        public RelayCommand ResetLeftTriggerCommand =>
            _resetLeftTrigger ??= new RelayCommand(() =>
            {
                LeftTriggerMode = AdaptiveTriggerMode.Off;
                LeftStartPosition = 0;
                LeftEndPosition = 255;
                LeftStrength = 200;
                LeftFrequency = 10;
            });
        private RelayCommand _resetLeftTrigger;

        public RelayCommand ResetRightTriggerCommand =>
            _resetRightTrigger ??= new RelayCommand(() =>
            {
                RightTriggerMode = AdaptiveTriggerMode.Off;
                RightStartPosition = 0;
                RightEndPosition = 255;
                RightStrength = 200;
                RightFrequency = 10;
            });
        private RelayCommand _resetRightTrigger;

        public RelayCommand ResetLeftRangeCommand =>
            _resetLeftRange ??= new RelayCommand(() =>
            {
                LeftStartPosition = 0;
                LeftEndPosition = 255;
            });
        private RelayCommand _resetLeftRange;

        public RelayCommand ResetRightRangeCommand =>
            _resetRightRange ??= new RelayCommand(() =>
            {
                RightStartPosition = 0;
                RightEndPosition = 255;
            });
        private RelayCommand _resetRightRange;

        public RelayCommand ResetLeftStrengthCommand =>
            _resetLeftStrength ??= new RelayCommand(() => LeftStrength = 200);
        private RelayCommand _resetLeftStrength;

        public RelayCommand ResetRightStrengthCommand =>
            _resetRightStrength ??= new RelayCommand(() => RightStrength = 200);
        private RelayCommand _resetRightStrength;

        public RelayCommand ResetLeftFrequencyCommand =>
            _resetLeftFrequency ??= new RelayCommand(() => LeftFrequency = 10);
        private RelayCommand _resetLeftFrequency;

        public RelayCommand ResetRightFrequencyCommand =>
            _resetRightFrequency ??= new RelayCommand(() => RightFrequency = 10);
        private RelayCommand _resetRightFrequency;

        // GameCube sub-preset for Weapon mode — fills the Range + Strength
        // sliders with byte values that match DualSenseSupport / DSY's
        // GameCube preset (start ≈ 56 %, end ≈ 63 %, max force) so the
        // physical click feel matches a real GameCube trigger. The user
        // can still tweak the sliders afterwards; this is a one-click
        // loader, not a lock.
        public RelayCommand ApplyLeftGameCubePresetCommand =>
            _applyLeftGameCubePreset ??= new RelayCommand(() =>
            {
                LeftStartPosition = 0x90; // 144 / 255 ≈ 56 %
                LeftEndPosition   = 0xA0; // 160 / 255 ≈ 63 %
                LeftStrength      = 0xFF; // max force
            });
        private RelayCommand _applyLeftGameCubePreset;

        public RelayCommand ApplyRightGameCubePresetCommand =>
            _applyRightGameCubePreset ??= new RelayCommand(() =>
            {
                RightStartPosition = 0x90;
                RightEndPosition   = 0xA0;
                RightStrength      = 0xFF;
            });
        private RelayCommand _applyRightGameCubePreset;

        /// <summary>Reset lightbar to the Sony player-1 default (solid blue).</summary>
        public RelayCommand ResetLightbarColorCommand =>
            _resetLightbar ??= new RelayCommand(() =>
            {
                LightbarRed = 0;
                LightbarGreen = 0;
                LightbarBlue = 0xFF;
            });
        private RelayCommand _resetLightbar;

        /// <summary>Section-level reset for the lightbar card on the
        /// Lighting tab. Restores every lightbar field to its initial
        /// value: base mode + per-press overlay + palette + base color +
        /// input-reactive color + period + smooth toggle + hold/decay
        /// timing + audio threshold colors + audio mid/high split and
        /// crossfade. Mirrors the catch-all "Reset All" buttons on the
        /// FFB / Sticks / Triggers tabs so users have one button to undo
        /// a slot's lighting tweaks without walking the per-row resets.</summary>
        public RelayCommand ResetLightbarAllCommand =>
            _resetLightbarAll ??= new RelayCommand(() =>
            {
                LightbarMode = LightbarMode.PlayerNumber;
                InputReactiveMode = InputReactiveMode.Off;
                LightbarRed = 0;
                LightbarGreen = 0;
                LightbarBlue = 0xFF;
                InputReactiveR = 0xFF;
                InputReactiveG = 0xFF;
                InputReactiveB = 0xFF;
                LightbarPeriodMs = 3000;
                LightbarColorCycleSmooth = true;
                LightbarRainbowBrightness = 100;
                LightbarBatteryLowR = 0xFF; LightbarBatteryLowG = 0; LightbarBatteryLowB = 0;
                LightbarBatteryHighR = 0; LightbarBatteryHighG = 0xFF; LightbarBatteryHighB = 0;
                LightbarInputHoldMs = 0;
                LightbarInputDecayMs = 600;
                AudioLightbarSensitivity = 4.0;
                AudioLowR = 0; AudioLowG = 0xFF; AudioLowB = 0;
                AudioMidR = 0xFF; AudioMidG = 0xFF; AudioMidB = 0;
                AudioHighR = 0xFF; AudioHighG = 0; AudioHighB = 0;
                AudioLowToMidPercent = 33;
                AudioMidToHighPercent = 66;
                AudioCrossFadePercent = 5.0;
                ResetPaletteCommand?.Execute(null);
                ResetInputReactivePaletteCommand?.Execute(null);
            });
        private RelayCommand _resetLightbarAll;

        /// <summary>Section-level reset for the indicator-LEDs card on
        /// the Lighting tab. Player number row brightness + mode + mic
        /// mute LED state. Mirrors the lightbar card's Reset All shape.</summary>
        public RelayCommand ResetIndicatorLedsAllCommand =>
            _resetIndicatorLedsAll ??= new RelayCommand(() =>
            {
                PlayerLedBrightness = PlayerLedBrightness.High;
                PlayerLedMode = PlayerLedMode.PlayerNumber;
                MicLedMode = MicLedMode.Off;
            });
        private RelayCommand _resetIndicatorLedsAll;

        public RelayCommand ResetLightbarRedCommand =>
            _resetLightbarR ??= new RelayCommand(() => LightbarRed = 0);
        private RelayCommand _resetLightbarR;

        public RelayCommand ResetLightbarGreenCommand =>
            _resetLightbarG ??= new RelayCommand(() => LightbarGreen = 0);
        private RelayCommand _resetLightbarG;

        public RelayCommand ResetLightbarBlueCommand =>
            _resetLightbarB ??= new RelayCommand(() => LightbarBlue = 0xFF);
        private RelayCommand _resetLightbarB;

        // ── Audio-lightbar threshold reset commands ──
        // Defaults match the FPS use case from issue #55: green low,
        // yellow mid, red high.
        public RelayCommand ResetAudioLowRCommand =>
            _resetAudLowR ??= new RelayCommand(() => AudioLowR = 0);
        private RelayCommand _resetAudLowR;
        public RelayCommand ResetAudioLowGCommand =>
            _resetAudLowG ??= new RelayCommand(() => AudioLowG = 0xFF);
        private RelayCommand _resetAudLowG;
        public RelayCommand ResetAudioLowBCommand =>
            _resetAudLowB ??= new RelayCommand(() => AudioLowB = 0);
        private RelayCommand _resetAudLowB;

        public RelayCommand ResetAudioMidRCommand =>
            _resetAudMidR ??= new RelayCommand(() => AudioMidR = 0xFF);
        private RelayCommand _resetAudMidR;
        public RelayCommand ResetAudioMidGCommand =>
            _resetAudMidG ??= new RelayCommand(() => AudioMidG = 0xFF);
        private RelayCommand _resetAudMidG;
        public RelayCommand ResetAudioMidBCommand =>
            _resetAudMidB ??= new RelayCommand(() => AudioMidB = 0);
        private RelayCommand _resetAudMidB;

        public RelayCommand ResetAudioHighRCommand =>
            _resetAudHighR ??= new RelayCommand(() => AudioHighR = 0xFF);
        private RelayCommand _resetAudHighR;
        public RelayCommand ResetAudioHighGCommand =>
            _resetAudHighG ??= new RelayCommand(() => AudioHighG = 0);
        private RelayCommand _resetAudHighG;
        public RelayCommand ResetAudioHighBCommand =>
            _resetAudHighB ??= new RelayCommand(() => AudioHighB = 0);
        private RelayCommand _resetAudHighB;

        // ── Lightbar mode-parameter resets ──
        // One-tap defaults for the per-mode parameter sliders / checkboxes
        // / the palette collection. Match the field initializers so a
        // reset always lands on the same value a fresh slot starts at.

        public RelayCommand ResetLightbarModeCommand =>
            _resetLightbarMode ??= new RelayCommand(() => LightbarMode = LightbarMode.PlayerNumber);
        private RelayCommand _resetLightbarMode;

        public RelayCommand ResetInputReactiveModeCommand =>
            _resetInputReactiveMode ??= new RelayCommand(() => InputReactiveMode = InputReactiveMode.Off);
        private RelayCommand _resetInputReactiveMode;

        public RelayCommand ResetInputReactiveRCommand =>
            _resetInputReactiveR ??= new RelayCommand(() => InputReactiveR = 0xFF);
        private RelayCommand _resetInputReactiveR;
        public RelayCommand ResetInputReactiveGCommand =>
            _resetInputReactiveG ??= new RelayCommand(() => InputReactiveG = 0xFF);
        private RelayCommand _resetInputReactiveG;
        public RelayCommand ResetInputReactiveBCommand =>
            _resetInputReactiveB ??= new RelayCommand(() => InputReactiveB = 0xFF);
        private RelayCommand _resetInputReactiveB;

        public RelayCommand ResetPlayerLedBrightnessCommand =>
            _resetPlayerLedBrightness ??= new RelayCommand(() => PlayerLedBrightness = PlayerLedBrightness.High);
        private RelayCommand _resetPlayerLedBrightness;

        public RelayCommand ResetPlayerLedModeCommand =>
            _resetPlayerLedMode ??= new RelayCommand(() => PlayerLedMode = PlayerLedMode.PlayerNumber);
        private RelayCommand _resetPlayerLedMode;

        public RelayCommand ResetMicLedModeCommand =>
            _resetMicLedMode ??= new RelayCommand(() => MicLedMode = MicLedMode.Off);
        private RelayCommand _resetMicLedMode;

        /// <summary>Section-level reset for the Guide Button LED card on
        /// the Lighting tab (#209). Mirrors the indicator-LEDs card's
        /// Reset All shape.</summary>
        public RelayCommand ResetGuideLedAllCommand =>
            _resetGuideLedAll ??= new RelayCommand(() =>
            {
                GuideLedMode = GuideLedMode.DeviceDefault;
                GuideLedBrightness = 100;
            });
        private RelayCommand _resetGuideLedAll;

        public RelayCommand ResetGuideLedModeCommand =>
            _resetGuideLedMode ??= new RelayCommand(() => GuideLedMode = GuideLedMode.DeviceDefault);
        private RelayCommand _resetGuideLedMode;

        public RelayCommand ResetGuideLedBrightnessCommand =>
            _resetGuideLedBrightness ??= new RelayCommand(() => GuideLedBrightness = 100);
        private RelayCommand _resetGuideLedBrightness;

        public RelayCommand ResetLightbarPeriodCommand =>
            _resetLightbarPeriod ??= new RelayCommand(() => LightbarPeriodMs = 3000);
        private RelayCommand _resetLightbarPeriod;

        public RelayCommand ResetLightbarRainbowBrightnessCommand =>
            _resetLightbarRainbowBrightness ??= new RelayCommand(() => LightbarRainbowBrightness = 100);
        private RelayCommand _resetLightbarRainbowBrightness;

        // Per-channel resets for the Battery low/high picker — matches
        // the inline ResetButtonTight layout used by the Static base
        // color and the audio-band pickers.
        public RelayCommand ResetLightbarBatteryLowRCommand =>
            _resetBatLowR ??= new RelayCommand(() => LightbarBatteryLowR = 0xFF);
        private RelayCommand _resetBatLowR;
        public RelayCommand ResetLightbarBatteryLowGCommand =>
            _resetBatLowG ??= new RelayCommand(() => LightbarBatteryLowG = 0);
        private RelayCommand _resetBatLowG;
        public RelayCommand ResetLightbarBatteryLowBCommand =>
            _resetBatLowB ??= new RelayCommand(() => LightbarBatteryLowB = 0);
        private RelayCommand _resetBatLowB;

        public RelayCommand ResetLightbarBatteryHighRCommand =>
            _resetBatHighR ??= new RelayCommand(() => LightbarBatteryHighR = 0);
        private RelayCommand _resetBatHighR;
        public RelayCommand ResetLightbarBatteryHighGCommand =>
            _resetBatHighG ??= new RelayCommand(() => LightbarBatteryHighG = 0xFF);
        private RelayCommand _resetBatHighG;
        public RelayCommand ResetLightbarBatteryHighBCommand =>
            _resetBatHighB ??= new RelayCommand(() => LightbarBatteryHighB = 0);
        private RelayCommand _resetBatHighB;

        public RelayCommand ResetLightbarInputHoldCommand =>
            _resetLightbarInputHold ??= new RelayCommand(() => LightbarInputHoldMs = 0);
        private RelayCommand _resetLightbarInputHold;

        public RelayCommand ResetLightbarInputDecayCommand =>
            _resetLightbarInputDecay ??= new RelayCommand(() => LightbarInputDecayMs = 600);
        private RelayCommand _resetLightbarInputDecay;

        public RelayCommand ResetLightbarColorCycleSmoothCommand =>
            _resetLightbarColorCycleSmooth ??= new RelayCommand(() => LightbarColorCycleSmooth = true);
        private RelayCommand _resetLightbarColorCycleSmooth;

        public RelayCommand ResetAudioLightbarSensitivityCommand =>
            _resetAudSens ??= new RelayCommand(() => AudioLightbarSensitivity = 4.0);
        private RelayCommand _resetAudSens;

        public RelayCommand ResetAudioLowToMidPercentCommand =>
            _resetAudLowMid ??= new RelayCommand(() => AudioLowToMidPercent = 33);
        private RelayCommand _resetAudLowMid;

        public RelayCommand ResetAudioMidToHighPercentCommand =>
            _resetAudMidHigh ??= new RelayCommand(() => AudioMidToHighPercent = 66);
        private RelayCommand _resetAudMidHigh;

        public RelayCommand ResetAudioCrossFadePercentCommand =>
            _resetAudCrossFade ??= new RelayCommand(() => AudioCrossFadePercent = 5.0);
        private RelayCommand _resetAudCrossFade;

        public RelayCommand ResetPaletteCommand =>
            _resetPalette ??= new RelayCommand(() =>
            {
                lock (_lightbarPaletteLock)
                {
                    _lightbarPalette.Clear();
                    _lightbarPalette.Add(new LightbarPaletteEntry(0xFF, 0x00, 0x00));
                    _lightbarPalette.Add(new LightbarPaletteEntry(0x00, 0xFF, 0x00));
                    _lightbarPalette.Add(new LightbarPaletteEntry(0x00, 0x00, 0xFF));
                    _lightbarPalette.Add(new LightbarPaletteEntry(0xFF, 0xFF, 0x00));
                }
            });
        private RelayCommand _resetPalette;
    }

    /// <summary>Sony's seven canonical adaptive trigger effect modes
    /// from the PS5 SDK (<c>ScePadTriggerEffectParam</c>). Wire-encoding
    /// of each mode into the 11-byte per-trigger payload happens in the
    /// synthesizer that lands in Commit 3.</summary>
    public enum AdaptiveTriggerMode
    {
        Off = 0,
        Feedback = 1,
        Weapon = 2,
        Vibration = 3,
        MultiplePositionFeedback = 4,
        SlopeFeedback = 5,
        MultiplePositionVibration = 6,
    }

    /// <summary>Mic mute LED mode. Values 0-2 map directly to byte 8
    /// (muteLedControl) per dualsense-tester's MuteButtonLedControl
    /// (0=Off, 1=Solid, 2=Pulse). FollowDeviceMute is resolved by the
    /// synthesizer via <c>AudioMuteService.GetMuteState</c> against
    /// <c>MicLedFollowDeviceId</c> — muted endpoint -> Solid (1),
    /// unmuted -> Off (0). Unknown / disconnected device falls back to
    /// Off so a stale config doesn't strand the LED in a wrong state.</summary>
    public enum MicLedMode
    {
        Off = 0,
        Solid = 1,
        Pulse = 2,
        FollowDeviceMute = 3,
    }

    /// <summary>One row in the mic-LED FollowDeviceMute device-picker
    /// ComboBox. Display carries the user-facing label (already prefixed
    /// with [In] / [Out]); Id is the CoreAudio endpoint string the
    /// synthesizer hands to <c>AudioMuteService.GetMuteState</c>.</summary>
    public sealed class MicLedDeviceItem
    {
        public string Id { get; }
        public string Display { get; }
        public MicLedDeviceItem(string id, string display) { Id = id; Display = display; }
    }

    /// <summary>Player indicator LED selection. Sequential 0-5 to map
    /// 1:1 with the ComboBox dropdown via <c>EnumIndexConverter</c>.
    /// The synthesizer translates these to the wire-form bit patterns
    /// at byte 43 (playerIndicator):
    /// Off=0x00, Player1=0x04, Player2=0x0A, Player3=0x15,
    /// Player4=0x1B, All=0x1F (per dualsense-tester's
    /// PlayerLedControl). The 0x20 no-fade flag is ORed in
    /// independently by the synthesizer.</summary>
    public enum PlayerLedMode
    {
        /// <summary>All pips dark. A deliberate choice since the
        /// PlayerNumber default landed: pre-v4 saves that stored Off
        /// meant "unset" and are lifted to PlayerNumber on load
        /// (LightingRev 0 migration in ApplyDeviceSlotConfigData).</summary>
        Off = 0,
        Player1 = 1,
        Player2 = 2,
        Player3 = 3,
        Player4 = 4,
        All = 5,
        /// <summary>Default (#191): the pips idle showing the virtual
        /// controller's player number. Appended after All. Macro cycle
        /// CSVs persist enum ints, so existing values never renumber.</summary>
        PlayerNumber = 6,
    }

    /// <summary>Player indicator brightness at byte 42 (ledBrightness).
    /// Firmware values are inverted from intuitive: 0=High, 2=Low.</summary>
    public enum PlayerLedBrightness
    {
        High = 0,
        Medium = 1,
        Low = 2,
    }

    /// <summary>Guide/Home button LED mode (discussion #209). Sequential
    /// 0-2 to map 1:1 with the ComboBox dropdown via
    /// <c>EnumIndexConverter</c>. DeviceDefault is the do-nothing default:
    /// PadForge never writes, so people who like the firmware's own
    /// brightness keep it untouched.</summary>
    public enum GuideLedMode
    {
        /// <summary>Write nothing, ever. The firmware value stands.</summary>
        DeviceDefault = 0,
        /// <summary>Hold <c>GuideLedBrightness</c>.</summary>
        Fixed = 1,
        /// <summary>Track the battery percent (fuller is brighter),
        /// floored at 10, re-applied on a slow cadence.</summary>
        Battery = 2,
    }

    /// <summary>One entry in the user-defined lightbar palette. Used by
    /// ColorCycle (walked over time) and InputReactive (cycled on each
    /// button press when randomize is off). ObservableObject so the
    /// dispatcher repaints whenever the user drags a slider on any entry
    /// — bubble PropertyChanged is wired in DeviceSlotConfig's
    /// constructor.</summary>
    public class LightbarPaletteEntry : ObservableObject
    {
        public LightbarPaletteEntry() { }
        public LightbarPaletteEntry(byte r, byte g, byte b)
        {
            _r = r; _g = g; _b = b;
        }

        private byte _r;
        public byte R { get => _r; set { if (SetProperty(ref _r, value)) OnPropertyChanged(nameof(Hex)); } }

        private byte _g;
        public byte G { get => _g; set { if (SetProperty(ref _g, value)) OnPropertyChanged(nameof(Hex)); } }

        private byte _b;
        public byte B { get => _b; set { if (SetProperty(ref _b, value)) OnPropertyChanged(nameof(Hex)); } }

        /// <summary>Two-way HEX shim. Get formats RRGGBB; set parses and
        /// writes through to R/G/B. Always fires PropertyChanged at the
        /// end so a TextBox bound with UpdateSourceTrigger=LostFocus
        /// re-displays the canonical form after invalid input.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public string Hex
        {
            get => $"{_r:X2}{_g:X2}{_b:X2}";
            set
            {
                if (!string.IsNullOrEmpty(value))
                {
                    var s = value.Trim();
                    if (s.StartsWith("#")) s = s.Substring(1);
                    if (s.Length == 6
                        && byte.TryParse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var nr)
                        && byte.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var ng)
                        && byte.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var nb))
                    {
                        R = nr; G = ng; B = nb;
                    }
                }
                OnPropertyChanged(nameof(Hex));
            }
        }
    }

    /// <summary>Unified lightbar effect picker. Replaces the legacy
    /// LightbarEnabled + AudioLightbarEnabled + AudioLightbarMode trio.
    /// Migration runs in SettingsService.ApplyDeviceSlotConfigs when
    /// the saved value is the default Off — old XML maps to Static,
    /// AudioPulse, AudioThresholds, AudioGradient, or AudioCrossFade
    /// based on which legacy bool was on.
    /// <para>Idle modes (Off, Static) only produce work on config
    /// changes. Animated modes (Breathing, Rainbow, ColorCycle, every
    /// Audio* variant, InputReactive) drive the dispatcher's periodic
    /// timer at ~30 Hz.</para></summary>
    public enum LightbarMode
    {
        Off = 0,
        Static = 1,
        Breathing = 2,
        Rainbow = 3,
        ColorCycle = 4,
        AudioPulse = 5,
        AudioPulseRandom = 6,
        AudioPulseRainbow = 7,
        AudioThresholds = 8,
        AudioGradient = 9,
        AudioCrossFade = 10,
        // ── Legacy (v3.1.x) ── these values stay in the enum for
        // XML round-trip + macro-action backward compat. They are NOT
        // exposed in the Lighting tab dropdown anymore — the dispatcher
        // migrates them on load and on macro apply into
        // InputReactiveMode (overlay) + LightbarMode = Off (base).
        // See SettingsService.ApplyDeviceSlotConfigData and
        // ApplyLightbarModeSetMigrated in the macro engine.
        InputReactive = 11,           // (legacy) random hue per press
        InputReactiveCycle = 12,      // (legacy) step through the configured palette per press
        InputReactiveFixed = 13,      // (legacy) single color (LightbarRed/Green/Blue) flashed per press
        // v3.3+ additions
        Battery = 14,                 // gradient between Battery Low / High colors driven by current battery level
        Strobe = 15,                  // square-wave on/off at LightbarPeriodMs cadence using LightbarRed/Green/Blue
        /// <summary>Default (#191): the lightbar idles showing the Sony
        /// player color for the virtual controller's number; a game
        /// writing lighting takes over and its last write persists for
        /// the session. Off (above) is now a deliberate hard-off that
        /// paints black. Pre-v4 saves that stored Off meant "unset" and
        /// are lifted to this value on load (LightingRev 0 migration).
        /// Appended after Strobe. Macro cycle CSVs persist enum ints,
        /// so existing values never renumber.</summary>
        PlayerNumber = 16,
    }

    /// <summary>Input-reactive overlay variant. Independent of the
    /// base <see cref="LightbarMode"/>: the overlay flashes the
    /// chosen color on top of whatever base mode the user picked,
    /// fading back to the base over the configured Hold + Decay
    /// window. Off disables the overlay entirely.</summary>
    public enum InputReactiveMode
    {
        Off = 0,
        /// <summary>Random hue rolled on each button press.</summary>
        Random = 1,
        /// <summary>Step through the configured lightbar palette
        /// on each button press.</summary>
        Cycle = 2,
        /// <summary>Flash the configured base RGB
        /// (<see cref="DeviceSlotConfig.LightbarRed"/> et al.)
        /// on each button press.</summary>
        Fixed = 3,
    }

    /// <summary>Audio-driven lightbar behavior. Issue #55 listed the
    /// threshold variant as primary and pulse-modulation as the
    /// alternative; PadForge ships both, plus two interpolation
    /// variants for the threshold path.</summary>
    public enum AudioLightbarMode
    {
        /// <summary>DSY-style brightness modulation: lightbar RGB =
        /// base color × audio peak. Pulses one color with audio.</summary>
        Pulse = 0,
        /// <summary>Three discrete colors with hard boundaries at the
        /// thresholds. Color snaps the moment the peak crosses.
        /// Issue #55 primary description.</summary>
        Thresholds = 1,
        /// <summary>Three colors, linearly interpolated across the peak
        /// range: 0 → Low, lowMid% → Mid, midHigh% → High. Above
        /// midHigh% stays at High. Smooth color transitions.</summary>
        Gradient = 2,
        /// <summary>Three discrete colors with a crossfade window
        /// around each threshold. Mostly the Thresholds behavior, but
        /// the boundary edges blend across <c>AudioCrossFadePercent</c>
        /// width to soften the snap.</summary>
        CrossFade = 3,
    }

    /// <summary>Serializable mirror of <see cref="DeviceSlotConfig"/>.
    /// XML round-trip via SettingsService. Fields use XmlAttribute to
    /// keep the serialized form compact and aligned with the adjacent
    /// per-slot config records.</summary>
    public class DeviceSlotConfigData
    {
        [XmlAttribute] public int SlotIndex { get; set; }
        /// <summary>Per-device entry: InstanceGuid of the physical
        /// device this Lighting tab config applies to. Empty
        /// (Guid.Empty serialized as "00000000-0000-0000-0000-000000000000")
        /// means a legacy slot-level entry — loader fans out to every
        /// mapped device on the slot.</summary>
        [XmlAttribute] public Guid DeviceGuid { get; set; }
        [XmlAttribute] public AdaptiveTriggerMode LeftTriggerMode { get; set; } = AdaptiveTriggerMode.Off;
        [XmlAttribute] public AdaptiveTriggerMode RightTriggerMode { get; set; } = AdaptiveTriggerMode.Off;
        [XmlAttribute] public byte LeftStartPosition { get; set; }
        [XmlAttribute] public byte LeftEndPosition { get; set; } = 255;
        [XmlAttribute] public byte LeftStrength { get; set; } = 200;
        [XmlAttribute] public byte LeftFrequency { get; set; } = 10;
        [XmlAttribute] public byte RightStartPosition { get; set; }
        [XmlAttribute] public byte RightEndPosition { get; set; } = 255;
        [XmlAttribute] public byte RightStrength { get; set; } = 200;
        [XmlAttribute] public byte RightFrequency { get; set; } = 10;
        [XmlAttribute] public byte LightbarRed { get; set; }
        [XmlAttribute] public byte LightbarGreen { get; set; }
        [XmlAttribute] public byte LightbarBlue { get; set; } = 0xFF;
        [XmlAttribute] public bool LightbarEnabled { get; set; }
        [XmlAttribute] public bool AudioPassthroughEnabled { get; set; }
        [XmlAttribute] public string AudioMirrorSourceId { get; set; } = string.Empty;
        // Haptic mirror engage gate (#185). Defaults match the VM: Always / 500 ms.
        [XmlAttribute] public string AudioMirrorEngageMode { get; set; } = "Always";
        [XmlAttribute] public string AudioMirrorEngageDeviceGuid { get; set; } = string.Empty;
        [XmlAttribute] public string AudioMirrorEngageButton { get; set; } = string.Empty;
        [XmlAttribute] public int AudioMirrorEngageReleaseMs { get; set; } = 500;
        // High-tone filter (#202). Defaults match the VM: Off / 800 Hz.
        [XmlAttribute] public string AudioToneFilterMode { get; set; } = "Off";
        [XmlAttribute] public int AudioToneLimitHz { get; set; } = 800;
        [XmlAttribute] public MicLedMode MicLedMode { get; set; } = MicLedMode.Off;
        [XmlAttribute] public string MicLedFollowDeviceId { get; set; } = string.Empty;
        [XmlAttribute] public PlayerLedMode PlayerLedMode { get; set; } = PlayerLedMode.Off;
        [XmlAttribute] public PlayerLedBrightness PlayerLedBrightness { get; set; } = PlayerLedBrightness.High;
        // Guide Button LED (#209). Defaults match the VM: DeviceDefault / 100,
        // so attribute-absent old saves load as "write nothing".
        [XmlAttribute] public GuideLedMode GuideLedMode { get; set; } = GuideLedMode.DeviceDefault;
        [XmlAttribute] public int GuideLedBrightness { get; set; } = 100;
        // Round-trip the legacy MicLightOn so old XML still loads. Mapped
        // to MicLedMode in the UI binding layer.
        [XmlAttribute] public bool MicLightOn { get; set; }

        // Audio-to-lightbar (Round 2)
        [XmlAttribute] public bool AudioLightbarEnabled { get; set; }
        [XmlAttribute] public double AudioLightbarSensitivity { get; set; } = 4.0;
        [XmlAttribute] public AudioLightbarMode AudioLightbarMode { get; set; } = AudioLightbarMode.Pulse;
        [XmlAttribute] public byte AudioLowR { get; set; } = 0x00;
        [XmlAttribute] public byte AudioLowG { get; set; } = 0xFF;
        [XmlAttribute] public byte AudioLowB { get; set; } = 0x00;
        [XmlAttribute] public byte AudioMidR { get; set; } = 0xFF;
        [XmlAttribute] public byte AudioMidG { get; set; } = 0xFF;
        [XmlAttribute] public byte AudioMidB { get; set; } = 0x00;
        [XmlAttribute] public byte AudioHighR { get; set; } = 0xFF;
        [XmlAttribute] public byte AudioHighG { get; set; } = 0x00;
        [XmlAttribute] public byte AudioHighB { get; set; } = 0x00;
        [XmlAttribute] public double AudioLowToMidPercent { get; set; } = 33;
        [XmlAttribute] public double AudioMidToHighPercent { get; set; } = 66;
        [XmlAttribute] public double AudioCrossFadePercent { get; set; } = 5.0;

        // Unified lightbar mode (v3.1.0+). When this is at the default
        // Off, SettingsService.ApplyDeviceSlotConfigs falls back to the
        // legacy LightbarEnabled / AudioLightbarEnabled / AudioLightbarMode
        // trio above to migrate old saves.
        [XmlAttribute] public LightbarMode LightbarMode { get; set; } = LightbarMode.Off;
        [XmlAttribute] public int LightbarPeriodMs { get; set; } = 3000;
        [XmlAttribute] public bool LightbarColorCycleSmooth { get; set; } = true;
        [XmlAttribute] public int LightbarRainbowBrightness { get; set; } = 100;
        [XmlAttribute] public byte LightbarBatteryLowR  { get; set; } = 0xFF;
        [XmlAttribute] public byte LightbarBatteryLowG  { get; set; } = 0x00;
        [XmlAttribute] public byte LightbarBatteryLowB  { get; set; } = 0x00;
        [XmlAttribute] public byte LightbarBatteryHighR { get; set; } = 0x00;
        [XmlAttribute] public byte LightbarBatteryHighG { get; set; } = 0xFF;
        [XmlAttribute] public byte LightbarBatteryHighB { get; set; } = 0x00;
        [XmlArray("LightbarPalette")]
        [XmlArrayItem("Color")]
        public LightbarPaletteEntryData[] LightbarPalette { get; set; }
        /// <summary>Dedicated palette for the InputReactive = Cycle overlay, separate from
        /// the ColorCycle palette above. Null on pre-split saves — load seeds it from
        /// <see cref="LightbarPalette"/> so existing setups keep their colors.</summary>
        [XmlArray("LightbarInputReactivePalette")]
        [XmlArrayItem("Color")]
        public LightbarPaletteEntryData[] LightbarInputReactivePalette { get; set; }
        [XmlAttribute] public int LightbarInputHoldMs { get; set; } = 0;
        [XmlAttribute] public int LightbarInputDecayMs { get; set; } = 600;
        [XmlAttribute] public bool LightbarInputRandomize { get; set; } = true;

        /// <summary>Input-reactive overlay variant (v3.2+). Independent
        /// of <see cref="LightbarMode"/> so users can layer a reactive
        /// flash over a static / animated base. Defaults to Off so
        /// older saves load with no behavior change.</summary>
        [XmlAttribute] public InputReactiveMode InputReactiveMode { get; set; } = InputReactiveMode.Off;

        /// <summary>Lighting schema revision (#191 follow-up). 0 = the
        /// save predates the PlayerNumber default, when Off doubled as
        /// "unset": the loader lifts Off to PlayerNumber for both
        /// LightbarMode and PlayerLedMode and still runs the v3.0
        /// LightbarEnabled / AudioLightbarEnabled fallback. 1 = the
        /// save wrote PlayerNumber-aware values: Off is a deliberate
        /// hard-off and every mode is taken literally. The enum
        /// defaults above stay at Off so attribute-absent old saves
        /// resolve through the same rev-0 path.</summary>
        [XmlAttribute] public int LightingRev { get; set; }

        /// <summary>Per-press flash color used by
        /// <see cref="InputReactiveMode.Fixed"/>. Kept separate from
        /// LightbarRed/Green/Blue so the base mode (Static / Breathing /
        /// etc.) and the reactive flash can each pick independent
        /// colors. Defaults to white.</summary>
        [XmlAttribute] public byte InputReactiveR { get; set; } = 0xFF;
        [XmlAttribute] public byte InputReactiveG { get; set; } = 0xFF;
        [XmlAttribute] public byte InputReactiveB { get; set; } = 0xFF;
    }

    /// <summary>Serializable mirror of <see cref="LightbarPaletteEntry"/>.
    /// Plain struct: three byte XmlAttributes per Color element.</summary>
    public class LightbarPaletteEntryData
    {
        [XmlAttribute] public byte R { get; set; }
        [XmlAttribute] public byte G { get; set; }
        [XmlAttribute] public byte B { get; set; }
    }
}
