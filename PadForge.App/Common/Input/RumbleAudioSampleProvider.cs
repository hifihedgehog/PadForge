using System;
using NAudio.Wave;
using PadForge.Engine.Common;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Allocation-free sine synthesizer for one rumble-to-audio endpoint
    /// (issue #236). Consumes ONLY the packed per-slot LFE state the poll
    /// thread publishes through <see cref="RumbleAudioService"/>; never
    /// reads VibrationStates, vibration meters, or any per-physical-device
    /// projection.
    ///
    /// <para>DSP contract (from the #234/#236 design rounds):</para>
    /// <list type="bullet">
    /// <item><description>Endpoint-shared sample clock: every voice derives
    /// its phase from one wrapped sample counter, so equal-frequency voices
    /// (the two 60 Hz trigger defaults, or two slots on one endpoint) stay
    /// phase-locked and can never cancel.</description></item>
    /// <item><description>One short click-suppression ramp per voice (a
    /// one-pole envelope, ~5 ms time constant). NO zero-hold: a game's
    /// square-wave PWM rumble (alternating hi / 0) must render as pulses,
    /// not get smeared into a sustained buzz.</description></item>
    /// <item><description>Plain summation, fixed conservative headroom,
    /// then a hard clamp on the ACTUAL composite sample. No envelope-sum
    /// normalizer: an amplitude-dependent AGC ducks an already-playing
    /// voice when a second one starts.</description></item>
    /// <item><description>No synthetic deadzone: the game-feedback value
    /// is digital, and a floor would suppress intentional weak rumble.
    /// Exact zero below an epsilon envelope so silence is bit-silent.</description></item>
    /// <item><description><see cref="Read"/> always fills and returns
    /// <c>count</c>; a starved or stopped producer renders silence, never
    /// a short read.</description></item>
    /// </list>
    /// </summary>
    internal sealed class RumbleAudioSampleProvider : IWaveProvider
    {
        /// <summary>48 kHz stereo float. Shared-mode WasapiOut passes
        /// AutoConvertPcm | SrcDefaultQuality (NAudio WasapiOut.Init), so
        /// the audio engine converts to any endpoint's mix format; stereo
        /// is the supported shaker-amp baseline and multichannel endpoints
        /// fold the pair through the Windows mixer.</summary>
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);

        private const int Rate = 48000;

        /// <summary>One-pole envelope coefficient per sample for a ~5 ms
        /// click-suppression ramp: alpha = 1/(tau*rate) with tau = 5 ms.
        /// (A 5 ms tau reaches ~95% in ~15 ms; the ramp exists to kill
        /// clicks, not to shape the game's dynamics.)</summary>
        private const float EnvAlpha = 1f / (0.005f * Rate);

        /// <summary>Fixed headroom ahead of the clamp. Four voices at full
        /// scale sum to 4.0; 0.25 headroom keeps the single-voice default
        /// loud while the composite clamp only engages on genuine
        /// multi-voice peaks. Not a hardware safety limit (amp gain and
        /// transducer impedance dominate); the user's gains sit upstream.</summary>
        private const float Headroom = 0.25f;

        /// <summary>Envelope floor under which a voice renders exact zero.</summary>
        private const float SilenceEpsilon = 1e-4f;

        /// <summary>Producer-death watchdog: a slot whose pack has not been
        /// republished for this long fades out via the normal envelope.
        /// The poll lane publishes every tick while the engine runs, so
        /// this only fires when the producer died without running its
        /// explicit silence edges. Callback inactivity never trips it,
        /// because the poll republishes the latched pack regardless of
        /// whether the game sent anything new.</summary>
        private const long WatchdogMs = 1000;

        /// <summary>One voice's immutable routing snapshot plus its mutable
        /// render state. Snapshots are rebuilt by the service on reconcile;
        /// envelopes carry across rebuilds keyed by (slot, voice).</summary>
        internal sealed class Voice
        {
            public int Slot;
            public int VoiceIndex;        // LfeOutputState voice 0..3
            public int FrequencyHz;       // clamped 20..120 by the builder
            public float Gain;            // voiceGain * masterGain, linear 0..1
            public bool ToLeft;
            public bool ToRight;
            public float Envelope;        // render-thread only
            public float TargetScratch;   // render-thread only, per-buffer
            public int CarrierScratch;    // render-thread only, per-buffer
                                          // (FrequencyHz, or the sweep override)
            public double PhaseAcc;       // render-thread only, radians
            public int LastCarrier;       // render-thread only; 0 = unseeded
        }

        // Swapped atomically by the service on reconcile; Read() reads the
        // reference once per call. Never mutated in place except the
        // render-thread-owned Envelope field.
        private volatile Voice[] _voices = Array.Empty<Voice>();

        // Endpoint-shared sample clock, wrapped at one second (Rate
        // samples). Integer carrier frequencies make the wrap phase-exact:
        // sin(2*pi*f*(n + Rate)/Rate) == sin(2*pi*f*n/Rate).
        private int _sampleClock;

        // Shutdown fade: when set, every voice targets zero regardless of
        // published packs, giving the service a bounded (~15 ms) ramp
        // before it stops and disposes the player.
        private volatile bool _fadeOut;

        /// <summary>Atomically installs a rebuilt voice set (reconcile).</summary>
        public void SetVoices(Voice[] voices) => _voices = voices ?? Array.Empty<Voice>();

        /// <summary>The current voice set (for envelope carry-over on rebuild).</summary>
        public Voice[] GetVoices() => _voices;

        /// <summary>Begin the shutdown fade; Read() ramps everything to
        /// silence within a few buffers.</summary>
        public void BeginFadeOut() => _fadeOut = true;

        public int Read(byte[] buffer, int offset, int count)
        {
            var voices = _voices;
            int frames = count / 8;   // 2 channels * 4 bytes
            long nowMs = Environment.TickCount64;
            bool fade = _fadeOut;

            // Per-buffer target update: one volatile pack read per slot,
            // then per-sample synthesis against the shared clock. 30 ms
            // buffers put target updates well above the game's own
            // feedback cadence.
            for (int v = 0; v < voices.Length; v++)
            {
                var vc = voices[v];
                float target = 0f;
                int carrier = vc.FrequencyHz;
                if (!fade)
                {
                    long pack = RumbleAudioService.ReadPack(vc.Slot);
                    if (pack != 0 && nowMs - RumbleAudioService.ReadLastPublishMs(vc.Slot) <= WatchdogMs)
                        target = LfeOutputState.Voice(pack, vc.VoiceIndex) / 65535f * vc.Gain;

                    // Test lane (UI test tone / sweep): mixed as extra
                    // target amplitude, never through the published packs,
                    // so provenance stays game-only on the mapping side.
                    long testPack = RumbleAudioService.ReadTestPack(vc.Slot, out int sweepHz);
                    if (testPack != 0)
                    {
                        float testAmp = LfeOutputState.Voice(testPack, vc.VoiceIndex) / 65535f * vc.Gain;
                        if (testAmp > target) target = testAmp;
                        if (sweepHz > 0 && testAmp > 0f) carrier = sweepHz;
                    }
                }
                vc.TargetScratch = target;
                vc.CarrierScratch = carrier;
            }

            // Phase discipline: at the STEADY carrier every voice's phase
            // accumulator is (re)seeded from the endpoint-shared sample
            // clock, so equal-frequency voices are identical-phase by
            // construction and can never cancel. Only the sweep override
            // free-runs its accumulator (a solo test tool); returning to
            // the steady carrier reseeds back onto the shared clock.
            for (int v = 0; v < voices.Length; v++)
            {
                var vc = voices[v];
                if (vc.LastCarrier != vc.CarrierScratch)
                {
                    if (vc.CarrierScratch == vc.FrequencyHz)
                        vc.PhaseAcc = 2.0 * Math.PI * vc.FrequencyHz * _sampleClock / Rate;
                    vc.LastCarrier = vc.CarrierScratch;
                }
            }

            int bi = offset;
            for (int n = 0; n < frames; n++)
            {
                float left = 0f, right = 0f;

                for (int v = 0; v < voices.Length; v++)
                {
                    var vc = voices[v];
                    // One-pole ramp toward the buffer's target.
                    float env = vc.Envelope;
                    env += (vc.TargetScratch - env) * EnvAlpha;
                    if (env < SilenceEpsilon && vc.TargetScratch <= 0f) env = 0f;
                    vc.Envelope = env;

                    // Phase advances whether or not the voice is audible,
                    // so silence never desynchronizes it from the shared
                    // clock and a resumed voice fades in phase-aligned.
                    double phase = vc.PhaseAcc + 2.0 * Math.PI * vc.CarrierScratch / Rate;
                    if (phase >= 2.0 * Math.PI) phase -= 2.0 * Math.PI;
                    vc.PhaseAcc = phase;
                    if (env == 0f) continue;

                    float sample = env * (float)Math.Sin(phase);
                    if (vc.ToLeft) left += sample;
                    if (vc.ToRight) right += sample;
                }

                _sampleClock = _sampleClock + 1 == Rate ? 0 : _sampleClock + 1;

                // Fixed headroom, then clamp the actual composite sample.
                left *= Headroom;
                right *= Headroom;
                if (left > 1f) left = 1f; else if (left < -1f) left = -1f;
                if (right > 1f) right = 1f; else if (right < -1f) right = -1f;

                WriteFloat(buffer, bi, left);
                WriteFloat(buffer, bi + 4, right);
                bi += 8;
            }

            // Fill any trailing bytes (count not divisible by the frame
            // size) with zeros so the contract "always fills count" holds.
            for (int i = offset + frames * 8; i < offset + count; i++)
                buffer[i] = 0;

            return count;
        }

        private static void WriteFloat(byte[] buffer, int index, float value)
        {
            // BitConverter.SingleToInt32Bits + manual store: no allocation,
            // no unsafe context.
            int bits = BitConverter.SingleToInt32Bits(value);
            buffer[index] = (byte)bits;
            buffer[index + 1] = (byte)(bits >> 8);
            buffer[index + 2] = (byte)(bits >> 16);
            buffer[index + 3] = (byte)(bits >> 24);
        }

        /// <summary>True once the shutdown fade has decayed every envelope
        /// to silence, so the service can stop the player click-free.</summary>
        public bool FadeComplete()
        {
            if (!_fadeOut) return false;
            var voices = _voices;
            for (int i = 0; i < voices.Length; i++)
                if (voices[i].Envelope > 0f) return false;
            return true;
        }
    }
}
