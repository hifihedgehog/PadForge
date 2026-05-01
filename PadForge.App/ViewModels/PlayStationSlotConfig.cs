using System;
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

        private byte _leftEndPosition;
        public byte LeftEndPosition
        {
            get => _leftEndPosition;
            set => SetProperty(ref _leftEndPosition, value);
        }

        private byte _leftStrength;
        public byte LeftStrength
        {
            get => _leftStrength;
            set => SetProperty(ref _leftStrength, value);
        }

        private byte _leftFrequency;
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

        private byte _rightEndPosition;
        public byte RightEndPosition
        {
            get => _rightEndPosition;
            set => SetProperty(ref _rightEndPosition, value);
        }

        private byte _rightStrength;
        public byte RightStrength
        {
            get => _rightStrength;
            set => SetProperty(ref _rightStrength, value);
        }

        private byte _rightFrequency;
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
        //  Audio bytes (DualSense only) — speaker / mic / mic light
        // ────────────────────────────────────────────────

        private byte _speakerVolume = 0x80;
        /// <summary>Controller speaker volume, 0-255. Default 0x80
        /// (mid-volume) — same as Sony's default for a fresh DualSense.</summary>
        public byte SpeakerVolume
        {
            get => _speakerVolume;
            set => SetProperty(ref _speakerVolume, value);
        }

        private bool _micMute;
        /// <summary>Controller microphone mute. Default false (unmuted).</summary>
        public bool MicMute
        {
            get => _micMute;
            set => SetProperty(ref _micMute, value);
        }

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
                LeftEndPosition = 0;
                LeftStrength = 0;
                LeftFrequency = 0;
            });
        private RelayCommand _resetLeftTrigger;

        public RelayCommand ResetRightTriggerCommand =>
            _resetRightTrigger ??= new RelayCommand(() =>
            {
                RightTriggerMode = AdaptiveTriggerMode.Off;
                RightStartPosition = 0;
                RightEndPosition = 0;
                RightStrength = 0;
                RightFrequency = 0;
            });
        private RelayCommand _resetRightTrigger;

        public RelayCommand ResetLeftRangeCommand =>
            _resetLeftRange ??= new RelayCommand(() =>
            {
                LeftStartPosition = 0;
                LeftEndPosition = 0;
            });
        private RelayCommand _resetLeftRange;

        public RelayCommand ResetRightRangeCommand =>
            _resetRightRange ??= new RelayCommand(() =>
            {
                RightStartPosition = 0;
                RightEndPosition = 0;
            });
        private RelayCommand _resetRightRange;

        public RelayCommand ResetLeftStrengthCommand =>
            _resetLeftStrength ??= new RelayCommand(() => LeftStrength = 0);
        private RelayCommand _resetLeftStrength;

        public RelayCommand ResetRightStrengthCommand =>
            _resetRightStrength ??= new RelayCommand(() => RightStrength = 0);
        private RelayCommand _resetRightStrength;

        public RelayCommand ResetLeftFrequencyCommand =>
            _resetLeftFrequency ??= new RelayCommand(() => LeftFrequency = 0);
        private RelayCommand _resetLeftFrequency;

        public RelayCommand ResetRightFrequencyCommand =>
            _resetRightFrequency ??= new RelayCommand(() => RightFrequency = 0);
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

        public RelayCommand ResetSpeakerVolumeCommand =>
            _resetSpeakerVol ??= new RelayCommand(() => SpeakerVolume = 0x80);
        private RelayCommand _resetSpeakerVol;
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
        [XmlAttribute] public byte LeftEndPosition { get; set; }
        [XmlAttribute] public byte LeftStrength { get; set; }
        [XmlAttribute] public byte LeftFrequency { get; set; }
        [XmlAttribute] public byte RightStartPosition { get; set; }
        [XmlAttribute] public byte RightEndPosition { get; set; }
        [XmlAttribute] public byte RightStrength { get; set; }
        [XmlAttribute] public byte RightFrequency { get; set; }
        [XmlAttribute] public byte LightbarRed { get; set; }
        [XmlAttribute] public byte LightbarGreen { get; set; }
        [XmlAttribute] public byte LightbarBlue { get; set; } = 0xFF;
        [XmlAttribute] public bool LightbarEnabled { get; set; }
        [XmlAttribute] public byte SpeakerVolume { get; set; } = 0x80;
        [XmlAttribute] public bool MicMute { get; set; }
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
    }
}
