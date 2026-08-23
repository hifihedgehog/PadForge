using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The clipboard's last uncarried family: rumble audio (#236),
    /// gamepad SOCD (#240) and Keep Awake (#270).
    ///
    /// <para>Every other container copy already carried these.
    /// CloneMappingSetDeep carried them for profile snapshots and the
    /// whole-slot Copy From carried them in process, so a user who used Copy
    /// From saw them travel and a user who used Copy / Paste did not, with a
    /// tooltip claiming to carry all settings either way.</para>
    ///
    /// <para>The reason it read as deliberate: ApplySlotMappingSetFromRows
    /// re-seeds the DESTINATION's copies across its fresh-set swap, which is
    /// correct on its own. That is a floor for callers carrying nothing, not
    /// an exclusion, and the carry leg simply did not exist.</para></summary>
    [Collection("SettingsManagerStatics")]
    public class SlotSetExtrasClipboardTests
    {
        private const int Slot = 0;

        private static MappingSet Authored() => new()
        {
            RumbleAudio = new RumbleAudioConfig
            {
                Enabled = true,
                EndpointId = "{0.0.0.00000000}.{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}",
                MasterGainPercent = 73,
                ChannelMode = "Stereo",
            },
            SocdMode = "Neutral",
            SocdPairs = "DPadLeft|DPadRight",
            KeepAwakeEnabled = true,
            KeepAwakeAxis = "LeftThumbX",
            KeepAwakeDeflection = 42,
            KeepAwakeMotion = true,
        };

        private static MappingSet Blank() => new();

        /// <summary>SlotMappingSets is global static state, so every test
        /// here restores what it found. Without this the suite's other
        /// SettingsManagerStatics members inherit a slot full of a bass-shaker
        /// config they never authored.</summary>
        private static IDisposable Seed(MappingSet ms)
        {
            var saved = (MappingSet[])SettingsManager.SlotMappingSets.Clone();
            SettingsManager.SlotMappingSets[Slot] = ms;
            return new Restore(saved);
        }

        /// <summary>Swap the slot again inside a test that already holds a
        /// Seed. The first Seed captured the original array, so only one
        /// restore is needed and a second would shadow it.</summary>
        private static void Reseed(MappingSet ms)
            => SettingsManager.SlotMappingSets[Slot] = ms;

        private sealed class Restore : IDisposable
        {
            private readonly MappingSet[] _saved;
            public Restore(MappingSet[] saved) { _saved = saved; }
            public void Dispose()
            {
                for (int i = 0; i < _saved.Length; i++)
                    SettingsManager.SlotMappingSets[i] = _saved[i];
            }
        }

        // ── copy side ───────────────────────────────────────────────────────

        [Fact]
        public void Build_CarriesEveryFieldOfTheFamily()
        {
            using var _s = Seed(Authored());
            string json = InputService.BuildSlotSetExtrasJson(Slot);
            Assert.False(string.IsNullOrEmpty(json));

            // Round-trip it through the applier onto a blank set: that proves
            // the JSON carries the values, not merely that it is non-empty.
            Reseed(Blank());
            InputService.ApplySlotSetExtrasJson(Slot, json, sameLayout: true);
            var got = SettingsManager.SlotMappingSets[Slot];

            Assert.NotNull(got.RumbleAudio);
            Assert.True(got.RumbleAudio.Enabled);
            Assert.Equal(73, got.RumbleAudio.MasterGainPercent);
            Assert.Equal("Stereo", got.RumbleAudio.ChannelMode);
            Assert.Equal("Neutral", got.SocdMode);
            Assert.Equal("DPadLeft|DPadRight", got.SocdPairs);
            Assert.True(got.KeepAwakeEnabled);
            Assert.Equal("LeftThumbX", got.KeepAwakeAxis);
            Assert.Equal(42, got.KeepAwakeDeflection);
            Assert.True(got.KeepAwakeMotion);
        }

        /// <summary>A slot that authored none of it produces no payload, so an
        /// ordinary Copy does not grow a block of defaults that would then
        /// overwrite a destination's real configuration with nothing.</summary>
        [Fact]
        public void Build_OnAnUnauthoredSlot_ProducesNothing()
        {
            using var _s = Seed(Blank());
            Assert.Null(InputService.BuildSlotSetExtrasJson(Slot));
        }

        /// <summary>Each field on its own keeps the payload alive. Written as
        /// a theory because the any-authoring gate is the kind of list a new
        /// field gets forgotten from.</summary>
        [Theory]
        [InlineData("rumble")]
        [InlineData("socdmode")]
        [InlineData("socdpairs")]
        [InlineData("keepawake")]
        [InlineData("keepawakeaxis")]
        [InlineData("keepawakedeflection")]
        [InlineData("keepawakemotion")]
        public void Build_AnySingleAuthoredField_ProducesAPayload(string which)
        {
            var ms = Blank();
            switch (which)
            {
                case "rumble": ms.RumbleAudio = new RumbleAudioConfig(); break;
                case "socdmode": ms.SocdMode = "Neutral"; break;
                case "socdpairs": ms.SocdPairs = "DPadLeft|DPadRight"; break;
                case "keepawake": ms.KeepAwakeEnabled = true; break;
                case "keepawakeaxis": ms.KeepAwakeAxis = "LeftThumbX"; break;
                case "keepawakedeflection": ms.KeepAwakeDeflection = 5; break;
                case "keepawakemotion": ms.KeepAwakeMotion = true; break;
            }
            using var _s = Seed(ms);
            Assert.False(string.IsNullOrEmpty(InputService.BuildSlotSetExtrasJson(Slot)));
        }

        // ── the endpoint contract ───────────────────────────────────────────

        /// <summary>EndpointId travels verbatim. Blanking it on a foreign
        /// machine would mean "system default render endpoint", which is
        /// exactly the route-bass-to-the-laptop-speakers outcome
        /// RumbleAudioConfig's fail-closed contract exists to prevent. An
        /// unresolved endpoint already fails closed with the selection
        /// preserved, so carrying it is both safe and the only option that
        /// keeps the user's choice when the config comes home.</summary>
        [Fact]
        public void Apply_CarriesTheEndpointVerbatim()
        {
            var src = Authored();
            using var _s = Seed(src);
            string json = InputService.BuildSlotSetExtrasJson(Slot);

            Reseed(Blank());
            InputService.ApplySlotSetExtrasJson(Slot, json, sameLayout: true);

            Assert.Equal(src.RumbleAudio.EndpointId,
                         SettingsManager.SlotMappingSets[Slot].RumbleAudio.EndpointId);
        }

        /// <summary>Source and destination end up with independent configs,
        /// so two slots cannot end up editing one shaker setup.
        ///
        /// <para>What actually guarantees that is the JSON boundary: Build
        /// returns a string and Apply takes one, so identity cannot survive
        /// the trip by signature. A mutation run confirmed as much, since
        /// removing either Clone() leaves this green. Both Clone() calls stay
        /// as belt and braces for a future caller that hands a snapshot across
        /// without the wire, and this test pins the property that matters to
        /// the user rather than the mechanism that currently provides
        /// it.</para></summary>
        [Fact]
        public void Apply_LeavesSourceAndDestinationIndependent()
        {
            var src = Authored();
            using var _s = Seed(src);
            string json = InputService.BuildSlotSetExtrasJson(Slot);

            Reseed(Blank());
            InputService.ApplySlotSetExtrasJson(Slot, json, sameLayout: true);
            var applied = SettingsManager.SlotMappingSets[Slot].RumbleAudio;

            Assert.NotSame(src.RumbleAudio, applied);
            applied.MasterGainPercent = 11;
            Assert.Equal(73, src.RumbleAudio.MasterGainPercent);
        }

        // ── the SOCD-only layout gate ───────────────────────────────────────

        /// <summary>SOCD's pair grammar is slot-type dependent (target names
        /// on a gamepad slot against Extended indices on an Extended one), so
        /// a cross-layout paste must not carry pairs that would parse to
        /// nothing. Rumble audio and Keep Awake name an endpoint and an axis,
        /// neither of which changes meaning with the output type, so
        /// blanket-gating the whole family would drop a bass-shaker setup for
        /// a reason that has nothing to do with bass shakers.</summary>
        [Fact]
        public void Apply_CrossLayout_KeepsTheDestinationsSocdAndCarriesTheRest()
        {
            using var _s = Seed(Authored());
            string json = InputService.BuildSlotSetExtrasJson(Slot);

            var dst = Blank();
            dst.SocdMode = "Off";
            dst.SocdPairs = "";
            Reseed(dst);

            InputService.ApplySlotSetExtrasJson(Slot, json, sameLayout: false);
            var got = SettingsManager.SlotMappingSets[Slot];

            Assert.Equal("Off", got.SocdMode);
            Assert.Equal("", got.SocdPairs);
            // Everything else still crossed.
            Assert.NotNull(got.RumbleAudio);
            Assert.True(got.RumbleAudio.Enabled);
            Assert.True(got.KeepAwakeEnabled);
            Assert.Equal("LeftThumbX", got.KeepAwakeAxis);
        }

        /// <summary>Positive control for the gate: on a same-layout paste the
        /// SOCD really does cross, so the assertion above is about the gate
        /// rather than about SOCD never crossing at all.</summary>
        [Fact]
        public void Apply_SameLayout_CarriesTheSocd()
        {
            using var _s = Seed(Authored());
            string json = InputService.BuildSlotSetExtrasJson(Slot);

            var dst = Blank();
            dst.SocdMode = "Off";
            Reseed(dst);

            InputService.ApplySlotSetExtrasJson(Slot, json, sameLayout: true);

            Assert.Equal("Neutral", SettingsManager.SlotMappingSets[Slot].SocdMode);
            Assert.Equal("DPadLeft|DPadRight", SettingsManager.SlotMappingSets[Slot].SocdPairs);
        }

        // ── the payload crosses the clipboard envelope ──────────────────────

        /// <summary>The field has to survive PadSetting's JSON envelope, which
        /// carries it as an opaque string under its own key. A build and apply
        /// that agree in process while the envelope drops the field would be
        /// the exact shape of the defect this fixes.</summary>
        [Fact]
        public void SlotSetExtras_SurvivesThePadSettingJsonEnvelope()
        {
            using var _s = Seed(Authored());
            var ps = new PadSetting { SlotSetExtrasJson = InputService.BuildSlotSetExtrasJson(Slot) };
            Assert.False(string.IsNullOrEmpty(ps.SlotSetExtrasJson));

            string wire = ps.ToJson(Engine.VirtualControllerType.Xbox, false);
            var back = PadSetting.FromJson(wire, out _, out _);

            Assert.NotNull(back);
            Assert.Equal(ps.SlotSetExtrasJson, back.SlotSetExtrasJson);

            Reseed(Blank());
            InputService.ApplySlotSetExtrasJson(Slot, back.SlotSetExtrasJson, sameLayout: true);
            Assert.True(SettingsManager.SlotMappingSets[Slot].KeepAwakeEnabled);
        }

        // ── malformed input is inert ────────────────────────────────────────

        [Fact]
        public void Apply_MalformedPayload_LeavesTheDestinationAlone()
        {
            var dst = Authored();
            using var _s = Seed(dst);
            InputService.ApplySlotSetExtrasJson(Slot, "{ not json", sameLayout: true);

            var got = SettingsManager.SlotMappingSets[Slot];
            Assert.Equal("Neutral", got.SocdMode);
            Assert.True(got.KeepAwakeEnabled);
            Assert.NotNull(got.RumbleAudio);
        }

        [Fact]
        public void Apply_EmptyPayload_LeavesTheDestinationAlone()
        {
            using var _s = Seed(Authored());
            InputService.ApplySlotSetExtrasJson(Slot, null, sameLayout: true);
            Assert.True(SettingsManager.SlotMappingSets[Slot].KeepAwakeEnabled);
        }
    }
}
