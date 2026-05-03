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
    public class PlayStationSlotConfig : ObservableObject
    {
        public PlayStationSlotConfig()
        {
            HookPalette(_lightbarPalette);
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
            set => SetProperty(ref _micLedMode, value);
        }

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

        private PlayerLedMode _playerLedMode;
        /// <summary>Bottom-row player indicator LEDs (1-5 small white
        /// LEDs below the touchpad). Off = all dark; PlayerN = the
        /// canonical player-slot pattern; All = every LED lit.
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

        // ────────────────────────────────────────────────
        //  Lightbar — unified mode picker. Replaces the old separate
        //  LightbarEnabled and AudioLightbarEnabled toggles. The legacy
        //  bools still exist below for XML round-trip and migration on
        //  load (SettingsService.ApplyPlayStationConfigs maps them into
        //  LightbarMode if LightbarMode is at its default).
        // ────────────────────────────────────────────────

        private LightbarMode _lightbarMode = LightbarMode.Off;
        /// <summary>Active lightbar effect. Off means PadForge does not
        /// author the lightbar at all (game owns it). Animated modes
        /// (Breathing / Rainbow / ColorCycle / Audio* / InputReactive)
        /// run on the dispatcher's periodic timer.</summary>
        public LightbarMode LightbarMode
        {
            get => _lightbarMode;
            set => SetProperty(ref _lightbarMode, value);
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

        // Variable-length palette shared by ColorCycle and InputReactive
        // modes. Defaults to four primaries (red, green, blue, yellow);
        // user can add or remove entries from the Lighting tab. Synth
        // iterates with idx % Count so any size from 1..N works.
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
                UnhookPalette(_lightbarPalette);
                _lightbarPalette = v;
                HookPalette(_lightbarPalette);
                OnPropertyChanged(nameof(LightbarPalette));
            }
        }

        public RelayCommand AddPaletteColorCommand =>
            _addPalette ??= new RelayCommand(() =>
            {
                // Roll a fresh hue distinct from the last entry. Keeps the
                // newly added swatch visually different from the one above
                // so the user can immediately see it landed.
                byte r = 0xFF, g = 0xFF, b = 0xFF;
                if (LightbarPalette.Count > 0)
                {
                    var last = LightbarPalette[LightbarPalette.Count - 1];
                    // Rotate primaries in a simple cycle to keep contrast.
                    if (last.R == 0xFF && last.G == 0x00 && last.B == 0x00) { r = 0x00; g = 0xFF; b = 0x00; }
                    else if (last.R == 0x00 && last.G == 0xFF && last.B == 0x00) { r = 0x00; g = 0x00; b = 0xFF; }
                    else if (last.R == 0x00 && last.G == 0x00 && last.B == 0xFF) { r = 0xFF; g = 0xFF; b = 0x00; }
                    else { r = 0xFF; g = 0x00; b = 0x00; }
                }
                LightbarPalette.Add(new LightbarPaletteEntry(r, g, b));
            });
        private RelayCommand _addPalette;

        public RelayCommand<LightbarPaletteEntry> RemovePaletteColorCommand =>
            _removePalette ??= new RelayCommand<LightbarPaletteEntry>(entry =>
            {
                if (entry == null) return;
                if (LightbarPalette.Count <= 1) return; // never let it go empty
                LightbarPalette.Remove(entry);
            });
        private RelayCommand<LightbarPaletteEntry> _removePalette;

        private int _lightbarInputDecayMs = 600;
        /// <summary>Decay time for InputReactive pulses, in milliseconds.
        /// A button press flashes the chosen color at full intensity, then
        /// fades to black over this duration.</summary>
        public int LightbarInputDecayMs
        {
            get => _lightbarInputDecayMs;
            set => SetProperty(ref _lightbarInputDecayMs, Math.Clamp(value, 100, 3000));
        }

        private bool _lightbarInputRandomize = true;
        /// <summary>InputReactive color source. True picks a random hue
        /// per press; false cycles through the 4-color palette in order.</summary>
        public bool LightbarInputRandomize
        {
            get => _lightbarInputRandomize;
            set => SetProperty(ref _lightbarInputRandomize, value);
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

        private bool _userEffectsEnabled;
        /// <summary>Master toggle for user-configured effect synthesis.
        /// When false, only Feature A (game-driven passthrough via
        /// <c>DualSensePassthroughDispatcher</c>) writes to the assigned
        /// physical DualSense. When true, the synthesizer also runs and
        /// PadForge writes the UI-configured trigger / lightbar / audio
        /// effects directly via <c>SDL_SendGamepadEffect</c>. Game writes
        /// always win per packet; user effects are the fallback layer.</summary>
        public bool UserEffectsEnabled
        {
            get => _userEffectsEnabled;
            set => SetProperty(ref _userEffectsEnabled, value);
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

        /// <summary>Reset lightbar to the Sony player-1 default (solid blue).</summary>
        public RelayCommand ResetLightbarColorCommand =>
            _resetLightbar ??= new RelayCommand(() =>
            {
                LightbarRed = 0;
                LightbarGreen = 0;
                LightbarBlue = 0xFF;
            });
        private RelayCommand _resetLightbar;

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

        public RelayCommand ResetLightbarPeriodCommand =>
            _resetLightbarPeriod ??= new RelayCommand(() => LightbarPeriodMs = 3000);
        private RelayCommand _resetLightbarPeriod;

        public RelayCommand ResetLightbarInputDecayCommand =>
            _resetLightbarInputDecay ??= new RelayCommand(() => LightbarInputDecayMs = 600);
        private RelayCommand _resetLightbarInputDecay;

        public RelayCommand ResetLightbarColorCycleSmoothCommand =>
            _resetLightbarColorCycleSmooth ??= new RelayCommand(() => LightbarColorCycleSmooth = true);
        private RelayCommand _resetLightbarColorCycleSmooth;

        public RelayCommand ResetLightbarInputRandomizeCommand =>
            _resetLightbarInputRandomize ??= new RelayCommand(() => LightbarInputRandomize = true);
        private RelayCommand _resetLightbarInputRandomize;

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
                LightbarPalette.Clear();
                LightbarPalette.Add(new LightbarPaletteEntry(0xFF, 0x00, 0x00));
                LightbarPalette.Add(new LightbarPaletteEntry(0x00, 0xFF, 0x00));
                LightbarPalette.Add(new LightbarPaletteEntry(0x00, 0x00, 0xFF));
                LightbarPalette.Add(new LightbarPaletteEntry(0xFF, 0xFF, 0x00));
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

    /// <summary>Mic mute LED mode. Maps directly to byte 8
    /// (muteLedControl) values per dualsense-tester's
    /// MuteButtonLedControl: 0=Off, 1=Solid, 2=Pulse.</summary>
    public enum MicLedMode
    {
        Off = 0,
        Solid = 1,
        Pulse = 2,
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
        Off = 0,
        Player1 = 1,
        Player2 = 2,
        Player3 = 3,
        Player4 = 4,
        All = 5,
    }

    /// <summary>Player indicator brightness at byte 42 (ledBrightness).
    /// Firmware values are inverted from intuitive: 0=High, 2=Low.</summary>
    public enum PlayerLedBrightness
    {
        High = 0,
        Medium = 1,
        Low = 2,
    }

    /// <summary>One entry in the user-defined lightbar palette. Used by
    /// ColorCycle (walked over time) and InputReactive (cycled on each
    /// button press when randomize is off). ObservableObject so the
    /// dispatcher repaints whenever the user drags a slider on any entry
    /// — bubble PropertyChanged is wired in PlayStationSlotConfig's
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
    /// Migration runs in SettingsService.ApplyPlayStationConfigs when
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
        InputReactive = 11,
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

    /// <summary>Serializable mirror of <see cref="PlayStationSlotConfig"/>.
    /// XML round-trip via SettingsService. Fields use XmlAttribute to
    /// keep the serialized form compact and aligned with the adjacent
    /// per-slot config records.</summary>
    public class PlayStationSlotConfigData
    {
        [XmlAttribute] public int SlotIndex { get; set; }
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
        [XmlAttribute] public MicLedMode MicLedMode { get; set; } = MicLedMode.Off;
        [XmlAttribute] public PlayerLedMode PlayerLedMode { get; set; } = PlayerLedMode.Off;
        [XmlAttribute] public PlayerLedBrightness PlayerLedBrightness { get; set; } = PlayerLedBrightness.High;
        // Round-trip the legacy MicLightOn so old XML still loads. Mapped
        // to MicLedMode in the UI binding layer.
        [XmlAttribute] public bool MicLightOn { get; set; }
        [XmlAttribute] public bool UserEffectsEnabled { get; set; }

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
        // Off, SettingsService.ApplyPlayStationConfigs falls back to the
        // legacy LightbarEnabled / AudioLightbarEnabled / AudioLightbarMode
        // trio above to migrate old saves.
        [XmlAttribute] public LightbarMode LightbarMode { get; set; } = LightbarMode.Off;
        [XmlAttribute] public int LightbarPeriodMs { get; set; } = 3000;
        [XmlAttribute] public bool LightbarColorCycleSmooth { get; set; } = true;
        [XmlArray("LightbarPalette")]
        [XmlArrayItem("Color")]
        public LightbarPaletteEntryData[] LightbarPalette { get; set; }
        [XmlAttribute] public int LightbarInputDecayMs { get; set; } = 600;
        [XmlAttribute] public bool LightbarInputRandomize { get; set; } = true;
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
