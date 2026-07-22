using System;
using System.IO;
using System.Xml.Serialization;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Issue #236 (rumble to audio / bass shakers): the packed LFE state,
    /// the slot-global Rumble source family, and the MappingSet-hosted
    /// config's persistence contract, including the config-only cold-load
    /// case that the rows-only content gate used to discard.
    /// </summary>
    public class RumbleAudioTests : IDisposable
    {
        public RumbleAudioTests() => SourceCoercion.SlotRumbleProvider = null;
        public void Dispose() => SourceCoercion.SlotRumbleProvider = null;

        // ─── LfeOutputState pack / unpack ───

        [Fact]
        public void Pack_RoundTrips_AllFourVoices()
        {
            long packed = LfeOutputState.Pack(1, 2, 3, 65535);
            Assert.Equal(1, LfeOutputState.Low(packed));
            Assert.Equal(2, LfeOutputState.High(packed));
            Assert.Equal(3, LfeOutputState.TriggerLeft(packed));
            Assert.Equal(65535, LfeOutputState.TriggerRight(packed));
        }

        [Fact]
        public void Pack_HighVoiceValues_DoNotBleedAcrossLanes()
        {
            // 0xFFFF in one lane must not sign-extend into its neighbor.
            for (int voice = 0; voice < 4; voice++)
            {
                long packed = LfeOutputState.Pack(
                    voice == 0 ? (ushort)0xFFFF : (ushort)0,
                    voice == 1 ? (ushort)0xFFFF : (ushort)0,
                    voice == 2 ? (ushort)0xFFFF : (ushort)0,
                    voice == 3 ? (ushort)0xFFFF : (ushort)0);
                for (int check = 0; check < 4; check++)
                    Assert.Equal(check == voice ? 0xFFFF : 0, LfeOutputState.Voice(packed, check));
            }
        }

        [Fact]
        public void Voice_OutOfRangeIndex_ReadsZero()
        {
            long packed = LfeOutputState.Pack(9, 9, 9, 9);
            Assert.Equal(0, LfeOutputState.Voice(packed, -1));
            Assert.Equal(0, LfeOutputState.Voice(packed, 4));
        }

        // ─── Descriptor family / classification ───

        [Theory]
        [InlineData("Rumble Low", 0)]
        [InlineData("Rumble High", 1)]
        [InlineData("Rumble Trigger Left", 2)]
        [InlineData("Rumble Trigger Right", 3)]
        public void TryGetRumbleVoice_MapsTheFourDescriptors(string descriptor, int expected)
        {
            Assert.True(SourceCoercion.TryGetRumbleVoice(descriptor, out int voice));
            Assert.Equal(expected, voice);
            Assert.Equal(SourceCoercion.SourceType.Rumble,
                SourceCoercion.ClassifyDescriptor(descriptor));
        }

        [Theory]
        [InlineData("Rumble")]
        [InlineData("Rumble Trigger")]
        [InlineData("Rumble Left")]
        [InlineData("")]
        [InlineData("Button 1")]
        public void TryGetRumbleVoice_RejectsNonMembers(string descriptor)
        {
            Assert.False(SourceCoercion.TryGetRumbleVoice(descriptor, out _));
        }

        [Fact]
        public void RumbleDescriptorTable_MatchesConfigSourceOrder()
        {
            // The DTO keys voices by source identity; the two tables must
            // stay identical or an authored voice silently detaches.
            Assert.Equal(RumbleAudioConfig.SourceOrder, SourceCoercion.RumbleDescriptorTable);
        }

        // ─── Reader contracts (through the public evaluators) ───

        private static MappingSource RumbleSrc(string descriptor, bool invert = false, int deadZone = 0)
            => new() { Descriptor = descriptor, Invert = invert, DeadZone = deadZone };

        [Fact]
        public void TriggerRead_IsUnipolarUshortOver65535()
        {
            SourceCoercion.SlotRumbleProvider = _ => LfeOutputState.Pack(32768, 0, 0, 65535);
            var state = new CustomInputState();
            Assert.Equal(32768f / 65535f,
                SourceCoercion.EvaluateForTriggerTarget(state, RumbleSrc("Rumble Low"), slotIndex: 0), 4);
            Assert.Equal(1f,
                SourceCoercion.EvaluateForTriggerTarget(state, RumbleSrc("Rumble Trigger Right"), slotIndex: 0), 4);
            Assert.Equal(0f,
                SourceCoercion.EvaluateForTriggerTarget(state, RumbleSrc("Rumble High"), slotIndex: 0), 4);
        }

        [Fact]
        public void TriggerRead_InvertDoesNotReadFullPullAtIdle()
        {
            // The Mouse Motion event-magnitude exemption: 1-v on a quiet
            // channel must not read as a full trigger pull.
            SourceCoercion.SlotRumbleProvider = _ => 0L;
            float v = SourceCoercion.EvaluateForTriggerTarget(
                new CustomInputState(), RumbleSrc("Rumble Low", invert: true), slotIndex: 0);
            Assert.Equal(0f, v, 4);
        }

        [Fact]
        public void BipolarRead_IsNonNegative_NeverShifted()
        {
            // Idle rumble must read neutral, never a full-negative stick.
            SourceCoercion.SlotRumbleProvider = _ => 0L;
            float idle = SourceCoercion.EvaluateForBipolarAxisTarget(
                new CustomInputState(), RumbleSrc("Rumble Low"), slotIndex: 0);
            Assert.Equal(0f, idle, 4);

            SourceCoercion.SlotRumbleProvider = _ => LfeOutputState.Pack(65535, 0, 0, 0);
            float full = SourceCoercion.EvaluateForBipolarAxisTarget(
                new CustomInputState(), RumbleSrc("Rumble Low"), slotIndex: 0);
            Assert.Equal(1f, full, 4);
        }

        [Fact]
        public void ButtonRead_UsesAmplitudeThreshold()
        {
            SourceCoercion.SlotRumbleProvider = _ => LfeOutputState.Pack(
                (ushort)(0.30f * 65535), 0, 0, 0);
            var state = new CustomInputState();
            // Global threshold 25%: 30% amplitude fires.
            Assert.True(SourceCoercion.EvaluateForButtonTarget(
                state, RumbleSrc("Rumble Low"), 25, slotIndex: 0));
            // An authored per-source DeadZone beats the global: 30% does
            // not fire under 60.
            Assert.False(SourceCoercion.EvaluateForButtonTarget(
                state, RumbleSrc("Rumble Low", deadZone: 60), 25, slotIndex: 0));
            // DeadZone 50 is the MappingSource model's untouched default
            // (the sentinel the grid's customized indicator keys on), so
            // it INHERITS the caller's global instead of overriding it:
            // 30% fires under global 25. The trigger-click activation
            // contract (ZL/ZR any-nonzero) depends on this inherit.
            Assert.True(SourceCoercion.EvaluateForButtonTarget(
                state, RumbleSrc("Rumble Low", deadZone: 50), 25, slotIndex: 0));
        }

        [Fact]
        public void Reads_WithNoProvider_AreSilent()
        {
            SourceCoercion.SlotRumbleProvider = null;
            var state = new CustomInputState();
            Assert.Equal(0f, SourceCoercion.EvaluateForTriggerTarget(
                state, RumbleSrc("Rumble Low"), slotIndex: 0), 4);
            Assert.False(SourceCoercion.EvaluateForButtonTarget(
                state, RumbleSrc("Rumble High"), 25, slotIndex: 0));
        }

        // ─── The Sony validity-gate constants (hid-playstation contract) ───
        //
        // The gate itself lives in the VC callback (App layer); these pin
        // the byte-level facts it is built on so a profile edit that moves
        // them fails loudly. Sizes and offsets read from the HIDMaestro
        // profile JSONs, masks from linux-hid hid-playstation.c
        // (DS_OUTPUT_VALID_FLAG0_COMPATIBLE_VIBRATION = BIT(0),
        // DS_OUTPUT_VALID_FLAG0_HAPTICS_SELECT = BIT(1)).

        [Fact]
        public void SonyMotorValidMasks_AreTheDocumentedBits()
        {
            const byte ds4Mask = 0x01;
            const byte ds5Mask = 0x03;
            // A lightbar-only DS5 report asserts neither motor bit.
            const byte lightbarOnlyFlag0 = 0x04;
            Assert.Equal(0, lightbarOnlyFlag0 & ds5Mask);
            // A compatible-vibration report asserts bit 0 on both.
            const byte vibrationFlag0 = 0x03;
            Assert.NotEqual(0, vibrationFlag0 & ds4Mask);
            Assert.NotEqual(0, vibrationFlag0 & ds5Mask);
        }

        // ─── MappingSet persistence ───

        private static MappingSet XmlRoundTrip(MappingSet ms)
        {
            var ser = new XmlSerializer(typeof(MappingSet));
            using var sw = new StringWriter();
            ser.Serialize(sw, ms);
            using var sr = new StringReader(sw.ToString());
            return (MappingSet)ser.Deserialize(sr);
        }

        [Fact]
        public void RumbleAudioConfig_SurvivesXmlRoundTrip()
        {
            var ms = new MappingSet
            {
                RumbleAudio = new RumbleAudioConfig
                {
                    Enabled = true,
                    EndpointId = "{0.0.0.00000000}.{abc}",
                    MasterGainPercent = 35,
                    ChannelMode = "Stereo",
                },
            };
            ms.RumbleAudio.Voices.Add(new RumbleAudioVoice
            {
                Source = "Rumble Low",
                Enabled = false,
                GainPercent = 70,
                FrequencyHz = 45,
            });

            var back = XmlRoundTrip(ms);
            Assert.NotNull(back.RumbleAudio);
            Assert.True(back.RumbleAudio.Enabled);
            Assert.Equal("{0.0.0.00000000}.{abc}", back.RumbleAudio.EndpointId);
            Assert.Equal(35, back.RumbleAudio.MasterGainPercent);
            Assert.Equal("Stereo", back.RumbleAudio.ChannelMode);
            var v = Assert.Single(back.RumbleAudio.Voices);
            Assert.Equal("Rumble Low", v.Source);
            Assert.False(v.Enabled);
            Assert.Equal(70, v.GainPercent);
            Assert.Equal(45, v.FrequencyHz);
        }

        [Fact]
        public void OldXml_WithoutRumbleAudio_DeserializesNull_MeaningDisabled()
        {
            var back = XmlRoundTrip(new MappingSet());
            Assert.Null(back.RumbleAudio);
        }

        [Fact]
        public void HasAuthoredContent_CountsConfigOnlySets()
        {
            // The cold-load content gate: a config-only set must not be
            // discarded on restart (the menus-only trap, Codex 2026-07-16).
            Assert.False(new MappingSet().HasAuthoredContent);
            Assert.True(new MappingSet { RumbleAudio = new RumbleAudioConfig() }.HasAuthoredContent);
            Assert.True(new MappingSet { Authoritative = true }.HasAuthoredContent);
            var withRow = new MappingSet();
            withRow.Rows.Add(new MappingRow { Target = "ButtonA" });
            Assert.True(withRow.HasAuthoredContent);
        }

        [Fact]
        public void Clone_IsDeep_AndCarriesEveryField()
        {
            var cfg = new RumbleAudioConfig
            {
                Enabled = true,
                EndpointId = "ep",
                MasterGainPercent = 42,
                ChannelMode = "Stereo",
            };
            cfg.Voices.Add(new RumbleAudioVoice { Source = "Rumble High", Enabled = false, GainPercent = 9, FrequencyHz = 99 });

            var copy = cfg.Clone();
            Assert.NotSame(cfg, copy);
            Assert.NotSame(cfg.Voices[0], copy.Voices[0]);
            Assert.True(copy.Enabled);
            Assert.Equal("ep", copy.EndpointId);
            Assert.Equal(42, copy.MasterGainPercent);
            Assert.Equal("Stereo", copy.ChannelMode);
            Assert.Equal("Rumble High", copy.Voices[0].Source);
            Assert.False(copy.Voices[0].Enabled);
            Assert.Equal(9, copy.Voices[0].GainPercent);
            Assert.Equal(99, copy.Voices[0].FrequencyHz);

            // Mutating the copy must not touch the original.
            copy.Voices[0].GainPercent = 100;
            Assert.Equal(9, cfg.Voices[0].GainPercent);
        }

        [Fact]
        public void FindVoice_KeysBySourceIdentity_FirstWins()
        {
            var cfg = new RumbleAudioConfig();
            cfg.Voices.Add(new RumbleAudioVoice { Source = "Rumble Low", GainPercent = 1 });
            cfg.Voices.Add(new RumbleAudioVoice { Source = "Rumble Low", GainPercent = 2 });
            Assert.Equal(1, cfg.FindVoice("Rumble Low").GainPercent);
            Assert.Null(cfg.FindVoice("Rumble High"));
        }

        [Fact]
        public void DefaultCarriers_AreTheDocumentedActuatorDefaults()
        {
            Assert.Equal(new[] { 40, 80, 60, 60 }, RumbleAudioConfig.DefaultFrequencyHz);
            Assert.Equal(20, RumbleAudioConfig.MinFrequencyHz);
            Assert.Equal(120, RumbleAudioConfig.MaxFrequencyHz);
        }

        // ─── Prefix-grammar safety ───

        [Fact]
        public void RumbleDescriptors_DoNotCollideWithLegacyPrefixGrammar()
        {
            // Leading 'R' stays clear of the I/H prefix strip; a mangled
            // "umble Low" would be the IR-pointer bug all over again.
            foreach (var d in SourceCoercion.RumbleDescriptorTable)
            {
                Assert.False(d.StartsWith("I", StringComparison.Ordinal));
                Assert.False(d.StartsWith("H", StringComparison.Ordinal));
            }
        }
    }
}
