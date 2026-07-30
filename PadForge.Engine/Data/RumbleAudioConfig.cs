using System.Collections.Generic;
using System.Xml.Serialization;

namespace PadForge.Engine.Data
{
    /// <summary>
    /// Per-slot rumble-to-audio (bass shaker / LFE) configuration, issue
    /// #236. Lives on <see cref="MappingSet.RumbleAudio"/> so the config
    /// shares one lifetime with the slot's rows across profile capture,
    /// apply, copy, compaction, reset, and .pfprofile export.
    ///
    /// <para>v1 routes the four inbound game-feedback channels the virtual
    /// controller RECEIVES (body low/high motors, left/right impulse
    /// triggers) 1:1 onto four sine voices keyed by SOURCE IDENTITY, never
    /// by list position. Each voice owns its enable, gain, and carrier
    /// frequency; placement is the channel mode plus the shared endpoint.
    /// A null config on the set means the feature is disabled for the
    /// slot and the renderer stays silent.</para>
    /// </summary>
    public class RumbleAudioConfig
    {
        /// <summary>Master enable. False keeps every voice silent while
        /// preserving the authored settings.</summary>
        [XmlAttribute] public bool Enabled { get; set; } = false;

        /// <summary>WASAPI render endpoint ID. Empty targets the system
        /// default render endpoint. A configured-but-unresolved endpoint
        /// FAILS CLOSED (no audio, selection preserved) rather than
        /// falling back to another device: routing bass to laptop
        /// speakers or headphones is worse than silence.</summary>
        [XmlAttribute] public string EndpointId { get; set; } = "";

        /// <summary>Master gain percent, 0..100, applied after per-voice
        /// gain. Conservative default keeps headroom for the four-voice
        /// sum ahead of the renderer's limiter.</summary>
        [XmlAttribute] public int MasterGainPercent { get; set; } = 50;

        /// <summary>Speaker placement: "" = mono (every voice to all
        /// channels), "Stereo" = controller stereo (low + left trigger
        /// voices to the left channel, high + right trigger voices to the
        /// right channel).</summary>
        [XmlAttribute] public string ChannelMode { get; set; } = "";

        /// <summary>The four voices, keyed by <see cref="RumbleAudioVoice.Source"/>.
        /// Missing entries read as that source's default voice; extra or
        /// duplicate entries are ignored by the renderer (first entry per
        /// source wins).</summary>
        [XmlElement("Voice")]
        public List<RumbleAudioVoice> Voices { get; set; } = new();

        /// <summary>The four fixed source descriptors, in voice order
        /// (low, high, trigger left, trigger right). The same strings the
        /// Engine's SourceCoercion Rumble family classifies.</summary>
        public static readonly string[] SourceOrder =
        {
            "Rumble Low",
            "Rumble High",
            "Rumble Trigger Left",
            "Rumble Trigger Right",
        };

        /// <summary>Default carrier frequency per voice index. Actuator
        /// identities, not verified audio frequencies: shaker and amp
        /// response varies wildly, so every voice is user-adjustable.
        /// Low 40 Hz, high 80 Hz, triggers 60 Hz.</summary>
        public static readonly int[] DefaultFrequencyHz = { 40, 80, 60, 60 };

        /// <summary>Carrier clamp floor (Hz).</summary>
        public const int MinFrequencyHz = 20;

        /// <summary>Carrier clamp ceiling (Hz).</summary>
        public const int MaxFrequencyHz = 120;

        /// <summary>Finds the authored voice for a source descriptor, or
        /// null when the config never authored one (callers substitute
        /// the defaults).</summary>
        public RumbleAudioVoice FindVoice(string source)
        {
            var voices = Voices;
            if (voices == null) return null;
            for (int i = 0; i < voices.Count; i++)
                if (voices[i] != null && voices[i].Source == source)
                    return voices[i];
            return null;
        }

        /// <summary>Deep copy for the container-copy family (profile
        /// snapshot clone, Copy From Slot, legacy merge). A new stamp or
        /// field added here is carried by every copier automatically.</summary>
        public RumbleAudioConfig Clone()
        {
            var copy = new RumbleAudioConfig
            {
                Enabled = Enabled,
                EndpointId = EndpointId ?? "",
                MasterGainPercent = MasterGainPercent,
                ChannelMode = ChannelMode ?? "",
            };
            if (Voices != null)
                foreach (var v in Voices)
                    if (v != null)
                        copy.Voices.Add(v.Clone());
            return copy;
        }
    }

    /// <summary>One sine voice of the rumble-to-audio config, keyed by
    /// its inbound feedback source descriptor.</summary>
    public class RumbleAudioVoice
    {
        /// <summary>Source identity, one of
        /// <see cref="RumbleAudioConfig.SourceOrder"/>.</summary>
        [XmlAttribute] public string Source { get; set; } = "";

        /// <summary>Per-voice enable. Gain 0 silences too; the explicit
        /// flag keeps the authored gain while muted.</summary>
        [XmlAttribute] public bool Enabled { get; set; } = true;

        /// <summary>Voice gain percent, 0..100.</summary>
        [XmlAttribute] public int GainPercent { get; set; } = 100;

        /// <summary>Sine carrier frequency in Hz, clamped by the renderer
        /// to <see cref="RumbleAudioConfig.MinFrequencyHz"/>..<see
        /// cref="RumbleAudioConfig.MaxFrequencyHz"/>.</summary>
        [XmlAttribute] public int FrequencyHz { get; set; } = 40;

        /// <summary>Field-for-field copy.</summary>
        public RumbleAudioVoice Clone() => new()
        {
            Source = Source ?? "",
            Enabled = Enabled,
            GainPercent = GainPercent,
            FrequencyHz = FrequencyHz,
        };
    }
}
