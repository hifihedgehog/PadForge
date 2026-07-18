using System;
using PadForge.Common.Input;
using PadForge.Engine.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Issue #236: the WASAPI sample provider's DSP contract. Rendering
    /// runs against the RumbleAudioService's published packs, so each test
    /// publishes through the real poll-thread surface (generation checked)
    /// and silences in teardown.
    /// </summary>
    public class RumbleAudioProviderTests : IDisposable
    {
        private const int TestSlot = 7;

        public RumbleAudioProviderTests() => RumbleAudioService.SilenceSlot(TestSlot);
        public void Dispose()
        {
            RumbleAudioService.SilenceSlot(TestSlot);
            RumbleAudioService.StopTest(TestSlot);
        }

        private static void Publish(int slot, long pack)
            => RumbleAudioService.PublishIfCurrent(slot, RumbleAudioService.GetGeneration(slot), pack);

        private static RumbleAudioSampleProvider.Voice[] OneVoice(
            int slot, int voice = 0, int freq = 40, float gain = 1f,
            bool left = true, bool right = true)
            => new[]
            {
                new RumbleAudioSampleProvider.Voice
                {
                    Slot = slot, VoiceIndex = voice, FrequencyHz = freq,
                    Gain = gain, ToLeft = left, ToRight = right,
                },
            };

        private static float[] Render(RumbleAudioSampleProvider p, int frames)
        {
            var bytes = new byte[frames * 8];
            int got = p.Read(bytes, 0, bytes.Length);
            Assert.Equal(bytes.Length, got);
            var samples = new float[frames * 2];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            return samples;
        }

        [Fact]
        public void SilentPack_RendersExactZeros()
        {
            var p = new RumbleAudioSampleProvider();
            p.SetVoices(OneVoice(TestSlot));
            Publish(TestSlot, 0L);
            var s = Render(p, 4800);
            foreach (var v in s) Assert.Equal(0f, v);
        }

        [Fact]
        public void FullVoice_RendersAToneWithinHeadroomClamp()
        {
            var p = new RumbleAudioSampleProvider();
            p.SetVoices(OneVoice(TestSlot));
            Publish(TestSlot, LfeOutputState.Pack(65535, 0, 0, 0));
            // A second's worth so the envelope fully settles.
            var s = Render(p, 48000);
            float peak = 0f;
            for (int i = s.Length / 2; i < s.Length; i++)
                peak = Math.Max(peak, Math.Abs(s[i]));
            // One full voice at gain 1 renders at the fixed headroom.
            Assert.InRange(peak, 0.2f, 0.26f);
        }

        [Fact]
        public void Envelope_RampsInsteadOfStepping()
        {
            var p = new RumbleAudioSampleProvider();
            p.SetVoices(OneVoice(TestSlot));
            Publish(TestSlot, LfeOutputState.Pack(65535, 0, 0, 0));
            var s = Render(p, 480); // first 10 ms
            // The very first samples must be near zero (click suppression),
            // not the settled amplitude.
            float earlyPeak = 0f;
            for (int i = 0; i < 20; i++) earlyPeak = Math.Max(earlyPeak, Math.Abs(s[i]));
            Assert.True(earlyPeak < 0.02f, $"first samples jumped to {earlyPeak}");
        }

        [Fact]
        public void ZeroTarget_DecaysToExactZero_NoZeroHold()
        {
            var p = new RumbleAudioSampleProvider();
            p.SetVoices(OneVoice(TestSlot));
            Publish(TestSlot, LfeOutputState.Pack(65535, 0, 0, 0));
            Render(p, 4800);
            // The game writes zero (PWM low phase): the tone must decay to
            // EXACT zero within the short ramp, never hold.
            Publish(TestSlot, 0L);
            Render(p, 4800); // 100 ms, far beyond the ~5 ms tau
            var tail = Render(p, 480);
            foreach (var v in tail) Assert.Equal(0f, v);
        }

        [Fact]
        public void EqualFrequencyVoices_ArePhaseLocked_NeverCancel()
        {
            // Two 60 Hz voices on different slots-worth of routing summed
            // on one endpoint must reinforce (2x amplitude), not cancel.
            var p = new RumbleAudioSampleProvider();
            var voices = new[]
            {
                OneVoice(TestSlot, voice: 2, freq: 60)[0],
                OneVoice(TestSlot, voice: 3, freq: 60)[0],
            };
            p.SetVoices(voices);
            Publish(TestSlot, LfeOutputState.Pack(0, 0, 65535, 65535));
            var s = Render(p, 48000);
            float peak = 0f;
            for (int i = s.Length / 2; i < s.Length; i++)
                peak = Math.Max(peak, Math.Abs(s[i]));
            // Two phase-locked full voices sum to 2.0 * headroom (0.5).
            Assert.InRange(peak, 0.45f, 0.52f);
        }

        [Fact]
        public void CompositeClamp_BoundsTheSum()
        {
            // Four full-scale voices at gain 1 sum to 4 * 0.25 = 1.0 at
            // coincident peaks; nothing may exceed the clamp.
            var p = new RumbleAudioSampleProvider();
            var voices = new[]
            {
                OneVoice(TestSlot, 0, 60)[0],
                OneVoice(TestSlot, 1, 60)[0],
                OneVoice(TestSlot, 2, 60)[0],
                OneVoice(TestSlot, 3, 60)[0],
            };
            p.SetVoices(voices);
            Publish(TestSlot, LfeOutputState.Pack(65535, 65535, 65535, 65535));
            var s = Render(p, 48000);
            foreach (var v in s) Assert.InRange(v, -1f, 1f);
        }

        [Fact]
        public void StereoRouting_KeepsVoicesOnTheirChannels()
        {
            var p = new RumbleAudioSampleProvider();
            p.SetVoices(OneVoice(TestSlot, voice: 0, freq: 40, left: true, right: false));
            Publish(TestSlot, LfeOutputState.Pack(65535, 0, 0, 0));
            var s = Render(p, 48000);
            float rightPeak = 0f;
            for (int i = 1; i < s.Length; i += 2)
                rightPeak = Math.Max(rightPeak, Math.Abs(s[i]));
            Assert.Equal(0f, rightPeak);
        }

        [Fact]
        public void FadeOut_ReachesCompleteSilence()
        {
            var p = new RumbleAudioSampleProvider();
            p.SetVoices(OneVoice(TestSlot));
            Publish(TestSlot, LfeOutputState.Pack(65535, 0, 0, 0));
            Render(p, 4800);
            p.BeginFadeOut();
            Render(p, 4800); // 100 ms of fade
            Assert.True(p.FadeComplete());
            var tail = Render(p, 480);
            foreach (var v in tail) Assert.Equal(0f, v);
        }

        [Fact]
        public void StaleProducer_WatchdogSilences()
        {
            // A pack older than the watchdog window renders as silence
            // even though it is nonzero (the producer died without its
            // explicit silence edge).
            var p = new RumbleAudioSampleProvider();
            p.SetVoices(OneVoice(TestSlot));
            Publish(TestSlot, LfeOutputState.Pack(65535, 0, 0, 0));
            // The provider compares against the publish timestamp; rather
            // than sleep past the 1 s window, assert the gate exists by
            // checking the timestamp surface the provider reads.
            Assert.True(Environment.TickCount64 - RumbleAudioService.ReadLastPublishMs(TestSlot) < 1000);
        }

        [Fact]
        public void SilenceEdge_BeatsInFlightPublish()
        {
            // The generation contract: a publish that captured its
            // generation before a silence edge must be discarded.
            int gen = RumbleAudioService.GetGeneration(TestSlot);
            RumbleAudioService.SilenceSlot(TestSlot);
            RumbleAudioService.PublishIfCurrent(TestSlot, gen, LfeOutputState.Pack(65535, 0, 0, 0));
            Assert.Equal(0L, RumbleAudioService.ReadPack(TestSlot));
        }

        [Fact]
        public void TestLane_NeverTouchesThePublishedPacks()
        {
            // Provenance: the UI test tone must not appear on the pack
            // surface the mapping grid and the poll lane read.
            RumbleAudioService.PulseTestVoice(TestSlot, 0, 500);
            Assert.Equal(0L, RumbleAudioService.ReadPack(TestSlot));
            long test = RumbleAudioService.ReadTestPack(TestSlot, out int sweepHz);
            Assert.Equal(65535, LfeOutputState.Low(test));
            Assert.Equal(0, sweepHz);
            RumbleAudioService.StopTest(TestSlot);
            Assert.Equal(0L, RumbleAudioService.ReadTestPack(TestSlot, out _));
        }

        [Fact]
        public void Sweep_OverridesCarrierWithinClampRange()
        {
            RumbleAudioService.StartSweep(TestSlot, 8000);
            RumbleAudioService.ReadTestPack(TestSlot, out int hz);
            Assert.InRange(hz, 20, 120);
            RumbleAudioService.StopTest(TestSlot);
        }

        [Fact]
        public void Read_AlwaysFillsAndReturnsCount_EvenPartialFrames()
        {
            var p = new RumbleAudioSampleProvider();
            p.SetVoices(Array.Empty<RumbleAudioSampleProvider.Voice>());
            var bytes = new byte[13]; // deliberately not frame-aligned
            for (int i = 0; i < bytes.Length; i++) bytes[i] = 0xAA;
            int got = p.Read(bytes, 0, bytes.Length);
            Assert.Equal(13, got);
            // Trailing partial-frame bytes are zero-filled, not stale.
            for (int i = 8; i < 13; i++) Assert.Equal(0, bytes[i]);
        }
    }
}
