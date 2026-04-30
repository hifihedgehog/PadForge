using System.Xml.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

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

        private bool _micLightOn;
        /// <summary>Mic mute LED state. Sony convention: lit when muted,
        /// off when unmuted. PadForge users may want to invert this for
        /// custom rigs — exposed as an independent toggle.</summary>
        public bool MicLightOn
        {
            get => _micLightOn;
            set => SetProperty(ref _micLightOn, value);
        }

        // ────────────────────────────────────────────────
        //  Master enable for Feature B (user-configured effects)
        // ────────────────────────────────────────────────

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
        [XmlAttribute] public bool MicLightOn { get; set; }
        [XmlAttribute] public bool UserEffectsEnabled { get; set; }
    }
}
