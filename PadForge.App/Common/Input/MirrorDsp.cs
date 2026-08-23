using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using NAudio.Dsp;

namespace PadForge.Common.Input
{
    /// <summary>One processing stage in the controller-audio chain (#347).
    ///
    /// <para>Three rules, and they are not style preferences. Every stage
    /// processes IN PLACE, preserves the frame count exactly, and looks ahead
    /// by nothing. The Bluetooth lane rate-matches its send cadence against an
    /// adaptive 20 to 100 ms cushion, so a stage that buffers, resamples or
    /// delays does not merely add latency, it fights that control loop. This
    /// path's entire failure history is buffer and timing assumptions (#320
    /// cracking, #325 the cushion, and 8024b879 where a clamp and the buffer
    /// it filled had to share a constant).</para>
    ///
    /// <para>No allocation once running. Coefficients are rebuilt off the
    /// audio thread and swapped in.</para></summary>
    internal interface IMirrorStage
    {
        /// <summary>False while the stage would be a no-op, so the chain can
        /// skip it without touching the buffer.</summary>
        bool Active { get; }

        /// <summary>Interleaved stereo, in place. <paramref name="frames"/> is
        /// pairs, so the span holds twice that many floats.</summary>
        void Process(Span<float> interleaved, int frames);

        /// <summary>Drop filter history at a stream discontinuity (transport
        /// flip, stall resync), so stale state cannot ring into new audio.</summary>
        void Reset();
    }

    /// <summary>Bauer stereophonic-to-binaural crossfeed, ported from Boris
    /// Mikhaylov's bs2b as vendored in openal-soft (core/bs2b.cpp, MIT).
    ///
    /// <para>Headphones present hard-panned material to one ear only, which
    /// never happens with speakers and is what makes some game audio fatiguing
    /// over the DualSense jack. Crossfeed mixes a lowpassed copy of each
    /// channel into the other and boosts the direct path to compensate.</para>
    ///
    /// <para>It carries NO delay line. The inter-aural timing comes out of the
    /// IIR phase response, so this stage adds exactly zero latency, which is
    /// what lets it sit in this lane at all.</para>
    ///
    /// <para>Harmless on the speaker paths by construction rather than by a
    /// special case. Those route a mono downmix, and with L equal to R the
    /// two channels stay equal through the filters, so nothing is "widened"
    /// into a driver that cannot represent it.</para>
    ///
    /// <para>The sum of the two filters is unity at DC, which is what bs2b's
    /// <c>g = 1 / (1 - G_hi + G_lo)</c> normalization exists for: the lowpass
    /// contributes <c>G_lo * g</c> and the highboost <c>(1 - G_hi) * g</c>,
    /// and those add to exactly 1. That makes DC gain a precise check on the
    /// whole coefficient derivation, and the tests use it as one.</para></summary>
    internal sealed class CrossfeedStage : IMirrorStage
    {
        internal const int Off = 0;
        internal const int LowCrossfeed = 1;
        internal const int MiddleCrossfeed = 2;
        internal const int HighCrossfeed = 3;
        internal const int LowEasy = 4;
        internal const int MiddleEasy = 5;
        internal const int HighEasy = 6;      // == BS2B_CMOY_CLEVEL
        internal const int JanMeier = 7;
        internal const int Bs2bDefault = 8;
        internal const int Custom = 9;
        internal const int MaxLevel = Custom;

        /// <summary>libbs2b's own limits for the two knobs, from bs2b.h.
        /// init() falls back to the default level when either is outside
        /// these, so clamping here keeps a custom setting from silently
        /// becoming something else.</summary>
        internal const int MinCutHz = 300;
        internal const int MaxCutHz = 2000;
        internal const double MinFeedDb = 1.0;
        internal const double MaxFeedDb = 15.0;

        /// <summary>Each level is a (cutoff Hz, feed dB) pair, which is what
        /// libbs2b actually stores: its level constants pack the cutoff in the
        /// low word and the feed in tenths of a dB in the high word.
        ///
        /// <para>Values 1 to 6 are libbs2b's six classic levels and keep their
        /// numbering, so a saved config does not shift meaning. 7 and 8 are the
        /// library's other two NAMED presets, which are not among the six:</para>
        ///
        /// <list type="bullet">
        /// <item>BS2B_CMOY_CLEVEL is 700 Hz / 6.0 dB, which IS High easy, so
        /// level 6 carries the C. Moy name rather than getting a duplicate.</item>
        /// <item>BS2B_JMEIER_CLEVEL is 650 Hz / 9.5 dB, a lower crossover with
        /// a much stronger feed than anything in the six. It had no
        /// representation here at all before.</item>
        /// <item>BS2B_DEFAULT_CLEVEL is 700 Hz / 4.5 dB, also not one of the
        /// six.</item>
        /// </list></summary>
        private static readonly (float FcLo, float FeedDb)[] Levels =
        {
            (0f, 0f),        // 0 unused
            (360f, 6.0f),    // 1 low
            (500f, 4.5f),    // 2 middle
            (700f, 3.0f),    // 3 high
            (360f, 8.4f),    // 4 low easy
            (500f, 7.2f),    // 5 middle easy
            (700f, 6.0f),    // 6 high easy == C. Moy
            (650f, 9.5f),    // 7 Jan Meier
            (700f, 4.5f),    // 8 bs2b default
        };

        /// <summary>libbs2b's own derivation, from bs2b.c init(). The six
        /// hardcoded quads openal-soft ships are exactly what this produces,
        /// verified to fifteen decimals on G_lo and G_hi for all six, so using
        /// the formula rather than the table costs no fidelity and buys the
        /// presets the table never held.</summary>
        private static (double FcHi, double GLo, double GHi) Derive(double fcLo, double feedDb)
        {
            double gbLo = feedDb * -5.0 / 6.0 - 3.0;
            double gbHi = feedDb / 6.0 - 3.0;
            double gLo = Math.Pow(10, gbLo / 20.0);
            double gHi = 1.0 - Math.Pow(10, gbHi / 20.0);
            double fcHi = fcLo * Math.Pow(2.0, (gbLo - 20.0 * Math.Log10(gHi)) / 12.0);
            return (fcHi, gLo, gHi);
        }

        private int _level;
        private int _rate;
        private float _a0Lo, _b1Lo, _a0Hi, _a1Hi, _b1Hi;
        // history[0] is the LEFT input's state, history[1] the RIGHT's.
        private float _lo0, _hi0, _lo1, _hi1;

        public bool Active => _level >= LowCrossfeed && _level <= MaxLevel;

        private float _customCut, _customFeed;

        /// <summary>Recomputes coefficients. Off the audio thread.
        ///
        /// <para><paramref name="customCutHz"/> and
        /// <paramref name="customFeedDb"/> are used only at
        /// <see cref="Custom"/>. That level is libbs2b's real API:
        /// bs2b_set_level_fcut and bs2b_set_level_feed set the two halves of a
        /// level independently, and the header marks the six classic presets
        /// obsolete in their favour.</para></summary>
        public void SetParams(int level, int sampleRate,
                              float customCutHz = 700f, float customFeedDb = 4.5f)
        {
            if (sampleRate < 1) return;
            if (level < LowCrossfeed || level > MaxLevel) { _level = Off; return; }

            float cut = (float)Math.Clamp(customCutHz, MinCutHz, MaxCutHz);
            float feed = (float)Math.Clamp(customFeedDb, MinFeedDb, MaxFeedDb);

            // A preset only needs rebuilding when the level or rate moves.
            // Custom also has to notice its own two knobs changing.
            if (level == _level && sampleRate == _rate
                && (level != Custom || (cut == _customCut && feed == _customFeed)))
                return;

            _level = level;
            _rate = sampleRate;
            _customCut = cut;
            _customFeed = feed;

            var (fcLoF, feedDb) = level == Custom ? (cut, feed) : Levels[level];
            var (fcHi, gLo, gHi) = Derive(fcLoF, feedDb);

            // The g normalization is openal-soft's, not upstream's, and it is
            // the reason the two filters sum to exactly unity at DC. The tests
            // pin that, so it stays.
            float g = (float)(1.0 / (1.0 - gHi + gLo));

            float x = (float)Math.Exp(-2.0 * Math.PI * fcLoF / sampleRate);
            _b1Lo = x;
            _a0Lo = (float)(gLo * (1.0 - x)) * g;

            x = (float)Math.Exp(-2.0 * Math.PI * fcHi / sampleRate);
            _b1Hi = x;
            _a0Hi = (float)(1.0 - gHi * (1.0 - x)) * g;
            _a1Hi = -x * g;

            Reset();
        }

        public void Reset() { _lo0 = _hi0 = _lo1 = _hi1 = 0f; }

        public void Process(Span<float> buf, int frames)
        {
            if (!Active) return;
            float a0lo = _a0Lo, b1lo = _b1Lo, a0hi = _a0Hi, a1hi = _a1Hi, b1hi = _b1Hi;
            float lo0 = _lo0, hi0 = _hi0, lo1 = _lo1, hi1 = _hi1;

            for (int i = 0, n = frames * 2; i < n; i += 2)
            {
                float xl = buf[i], xr = buf[i + 1];

                // Left input: highboost stays on the left, lowpass crosses right.
                float y0l = a0hi * xl + hi0;
                hi0 = a1hi * xl + b1hi * y0l;
                float y1l = a0lo * xl + lo0;
                lo0 = b1lo * y1l;

                // Right input: lowpass crosses left, highboost stays right.
                float y0r = a0lo * xr + lo1;
                lo1 = b1lo * y0r;
                float y1r = a0hi * xr + hi1;
                hi1 = a1hi * xr + b1hi * y1r;

                buf[i] = y0l + y0r;
                buf[i + 1] = y1l + y1r;
            }

            _lo0 = Flush(lo0); _hi0 = Flush(hi0);
            _lo1 = Flush(lo1); _hi1 = Flush(hi1);
        }

        /// <summary>Denormals in a decaying IIR are a real penalty on the
        /// Atom x5-Z8350 in the perf floor, so history is flushed to zero
        /// rather than allowed to drift into the denormal range.</summary>
        private static float Flush(float v) => Math.Abs(v) < 1e-20f ? 0f : v;
    }

    /// <summary>One parametric EQ band. Types match what AutoEq emits and what
    /// <see cref="BiQuadFilter"/> provides.</summary>
    public enum EqBandType
    {
        Peaking = 0,
        LowShelf = 1,
        HighShelf = 2,
        HighPass = 3,
        LowPass = 4,
        Notch = 5,
    }

    public sealed class EqBand
    {
        /// <summary>The highest band frequency the DSP will honour, as a
        /// fraction of the sample rate. A band at or above Nyquist is not a
        /// filter, it is an exception waiting to happen inside the cookbook
        /// math, so <see cref="ParametricEqStage"/> clamps here. Exposed so
        /// the editor can clamp to the SAME place rather than to a rounder
        /// number: its own doc claims the clamps match, and they did not.
        /// </summary>
        public const float MaxFreqFraction = 0.45f;

        public static float MaxFrequencyHz(int sampleRate) => sampleRate * MaxFreqFraction;

        /// <summary>The rate the chain runs at. Both Sony transports resample
        /// to it, so a band's ceiling does not vary by device.
        ///
        /// <para>Here rather than repeated at each consumer: the editor's
        /// clamp, the curve control's response and the engine's filters all
        /// have to agree on it, and two of the three getting their own copy is
        /// how the clamp drifted apart in the first place.</para></summary>
        public const int MirrorSampleRate = 48000;

        /// <summary>The ceiling at the rate the chain actually runs at.</summary>
        public static float MaxFrequencyHz() => MaxFrequencyHz(MirrorSampleRate);

        public bool Enabled = true;
        public EqBandType Type = EqBandType.Peaking;
        public float FrequencyHz = 1000f;
        public float GainDb;
        public float Q = 0.707f;

        public EqBand Clone() => new EqBand
        {
            Enabled = Enabled, Type = Type, FrequencyHz = FrequencyHz, GainDb = GainDb, Q = Q,
        };
    }

    /// <summary>Parametric EQ over NAudio's RBJ-cookbook
    /// <see cref="BiQuadFilter"/>, which already ships in NAudio.Core and is
    /// already attributed. Two filter instances per band, one per channel,
    /// because a biquad carries per-channel state.
    ///
    /// <para>Coefficients are built into a NEW array off the audio thread and
    /// swapped by reference, so a band edit can never tear a filter mid-frame
    /// or click from a half-applied coefficient set.</para></summary>
    internal sealed class ParametricEqStage : IMirrorStage
    {
        private sealed class Compiled
        {
            public BiQuadFilter[] L = Array.Empty<BiQuadFilter>();
            public BiQuadFilter[] R = Array.Empty<BiQuadFilter>();
        }

        private volatile Compiled _c = new Compiled();

        public bool Active => _c.L.Length > 0;

        public void SetBands(IReadOnlyList<EqBand> bands, int sampleRate)
        {
            // Kept so Reset can rebuild from them. The list is cloned by the
            // caller's decode and never mutated after, so holding it is safe.
            _bands = bands;
            _rate = sampleRate;
            if (bands == null || bands.Count == 0 || sampleRate < 1)
            {
                _c = new Compiled();
                return;
            }
            var l = new List<BiQuadFilter>(bands.Count);
            var r = new List<BiQuadFilter>(bands.Count);
            foreach (var b in bands)
            {
                if (b == null || !b.Enabled) continue;
                // A band at or above Nyquist is not a filter, it is an
                // exception waiting to happen inside the cookbook math.
                float f = Math.Clamp(b.FrequencyHz, 10f, EqBand.MaxFrequencyHz(sampleRate));
                float q = Math.Clamp(b.Q, 0.05f, 20f);
                float gain = Math.Clamp(b.GainDb, -30f, 30f);
                // Peaking and shelves are the only types that take gain; the
                // pass and notch types would silently ignore it.
                if (b.Type is EqBandType.Peaking or EqBandType.LowShelf or EqBandType.HighShelf
                    && Math.Abs(gain) < 0.01f)
                    continue;   // a 0 dB peak is the identity, so do not pay for it
                l.Add(Make(b.Type, sampleRate, f, q, gain));
                r.Add(Make(b.Type, sampleRate, f, q, gain));
            }
            _c = new Compiled { L = l.ToArray(), R = r.ToArray() };
        }

        private static BiQuadFilter Make(EqBandType t, int rate, float f, float q, float gainDb)
            => t switch
            {
                EqBandType.LowShelf => BiQuadFilter.LowShelf(rate, f, q, gainDb),
                EqBandType.HighShelf => BiQuadFilter.HighShelf(rate, f, q, gainDb),
                EqBandType.HighPass => BiQuadFilter.HighPassFilter(rate, f, q),
                EqBandType.LowPass => BiQuadFilter.LowPassFilter(rate, f, q),
                EqBandType.Notch => BiQuadFilter.NotchFilter(rate, f, q),
                _ => BiQuadFilter.PeakingEQ(rate, f, q, gainDb),
            };

        private IReadOnlyList<EqBand> _bands;
        private int _rate;

        public void Reset()
        {
            // BiQuadFilter exposes no history reset and its state is private,
            // so the only way to drop the history is to build new filters.
            // Carrying the SAME instances into a new Compiled, which is what
            // this did, carried their history with them and reset nothing at
            // all while reading as though it had.
            if (_bands != null) SetBands(_bands, _rate);
        }

        public void Process(Span<float> buf, int frames)
        {
            var c = _c;
            var fl = c.L; var fr = c.R;
            if (fl.Length == 0) return;
            for (int i = 0, n = frames * 2; i < n; i += 2)
            {
                float l = buf[i], r = buf[i + 1];
                for (int b = 0; b < fl.Length; b++) l = fl[b].Transform(l);
                for (int b = 0; b < fr.Length; b++) r = fr[b].Transform(r);
                buf[i] = l;
                buf[i + 1] = r;
            }
        }
    }

    /// <summary>Feed-forward peak limiter, no lookahead.
    ///
    /// <para>Structural rather than cosmetic. This chain sits UPSTREAM of the
    /// Opus encoder on Bluetooth and the WASAPI render on USB, so any EQ band
    /// with positive gain eats headroom the encoder then has to deal with, and
    /// Opus clipping sounds far worse than the EQ sounds better.</para>
    ///
    /// <para>Without lookahead a fast transient can pass before the envelope
    /// reacts, so the ceiling is additionally enforced by a hard clamp. That
    /// clamp is the guarantee the tests pin: gain riding reduces how often it
    /// is reached, it is not what makes the ceiling true.</para></summary>
    internal sealed class LimiterStage : IMirrorStage
    {
        private float _ceiling = 0.98f;
        private float _env;
        private float _atk = 0.01f, _rel = 0.0005f;
        private bool _on;

        public bool Active => _on;

        public void SetParams(bool enabled, float ceiling, int sampleRate,
                              float attackMs = 1.0f, float releaseMs = 80f)
        {
            _on = enabled;
            _ceiling = Math.Clamp(ceiling, 0.05f, 1f);
            if (sampleRate < 1) return;
            _atk = 1f - (float)Math.Exp(-1.0 / (Math.Max(0.05, attackMs) * 0.001 * sampleRate));
            _rel = 1f - (float)Math.Exp(-1.0 / (Math.Max(1.0, releaseMs) * 0.001 * sampleRate));
        }

        public void Reset() { _env = 0f; }

        public void Process(Span<float> buf, int frames)
        {
            if (!_on) return;
            float ceil = _ceiling, env = _env, atk = _atk, rel = _rel;
            for (int i = 0, n = frames * 2; i < n; i += 2)
            {
                float l = buf[i], r = buf[i + 1];
                float peak = Math.Max(Math.Abs(l), Math.Abs(r));
                // Attack fast toward a rising peak, release slowly.
                env += (peak > env ? atk : rel) * (peak - env);
                float gain = env > ceil ? ceil / env : 1f;
                l *= gain; r *= gain;
                // The ceiling is a guarantee, not an aspiration.
                buf[i] = Math.Clamp(l, -ceil, ceil);
                buf[i + 1] = Math.Clamp(r, -ceil, ceil);
            }
            _env = env < 1e-20f ? 0f : env;
        }
    }

    /// <summary>Magnitude response of a band list, for drawing the curve.
    ///
    /// <para>The RBJ cookbook coefficients are recomputed here rather than
    /// read out of <see cref="BiQuadFilter"/>, whose coefficients are private.
    /// Same formulas, so the drawn curve is the curve that gets applied, and
    /// the display does not depend on NAudio internals staying private or
    /// otherwise.</para></summary>
    public static class EqResponse
    {
        /// <summary>Summed response of every enabled band at one frequency,
        /// in dB. Pass and notch types contribute their real attenuation, so
        /// the curve shows what those actually do rather than drawing them
        /// flat.</summary>
        public static double MagnitudeDb(IReadOnlyList<EqBand> bands, double freqHz, int sampleRate)
        {
            if (bands == null || bands.Count == 0) return 0;
            double total = 1.0;
            foreach (var b in bands)
            {
                if (b == null || !b.Enabled) continue;
                total *= Magnitude(b, freqHz, sampleRate);
            }
            return 20.0 * Math.Log10(Math.Max(total, 1e-6));
        }

        /// <summary>Linear magnitude of one band at one frequency.</summary>
        public static double Magnitude(EqBand b, double freqHz, int sampleRate)
        {
            double w0 = 2 * Math.PI * Math.Clamp(b.FrequencyHz, 10f, EqBand.MaxFrequencyHz(sampleRate)) / sampleRate;
            double q = Math.Clamp(b.Q, 0.05f, 20f);
            double alpha = Math.Sin(w0) / (2 * q);
            double cw = Math.Cos(w0);
            double A = Math.Pow(10, Math.Clamp(b.GainDb, -30f, 30f) / 40.0);
            double b0, b1, b2, a0, a1, a2;

            switch (b.Type)
            {
                case EqBandType.LowShelf:
                {
                    double s = 2 * Math.Sqrt(A) * alpha;
                    b0 = A * ((A + 1) - (A - 1) * cw + s);
                    b1 = 2 * A * ((A - 1) - (A + 1) * cw);
                    b2 = A * ((A + 1) - (A - 1) * cw - s);
                    a0 = (A + 1) + (A - 1) * cw + s;
                    a1 = -2 * ((A - 1) + (A + 1) * cw);
                    a2 = (A + 1) + (A - 1) * cw - s;
                    break;
                }
                case EqBandType.HighShelf:
                {
                    double s = 2 * Math.Sqrt(A) * alpha;
                    b0 = A * ((A + 1) + (A - 1) * cw + s);
                    b1 = -2 * A * ((A - 1) + (A + 1) * cw);
                    b2 = A * ((A + 1) + (A - 1) * cw - s);
                    a0 = (A + 1) - (A - 1) * cw + s;
                    a1 = 2 * ((A - 1) - (A + 1) * cw);
                    a2 = (A + 1) - (A - 1) * cw - s;
                    break;
                }
                case EqBandType.LowPass:
                    b0 = (1 - cw) / 2; b1 = 1 - cw; b2 = (1 - cw) / 2;
                    a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
                    break;
                case EqBandType.HighPass:
                    b0 = (1 + cw) / 2; b1 = -(1 + cw); b2 = (1 + cw) / 2;
                    a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
                    break;
                case EqBandType.Notch:
                    b0 = 1; b1 = -2 * cw; b2 = 1;
                    a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
                    break;
                default:    // Peaking
                    b0 = 1 + alpha * A; b1 = -2 * cw; b2 = 1 - alpha * A;
                    a0 = 1 + alpha / A; a1 = -2 * cw; a2 = 1 - alpha / A;
                    break;
            }

            // |H(e^jw)| at the evaluation frequency.
            double w = 2 * Math.PI * Math.Clamp(freqHz, 1.0, sampleRate * 0.5 - 1) / sampleRate;
            double cos1 = Math.Cos(w), sin1 = Math.Sin(w);
            double cos2 = Math.Cos(2 * w), sin2 = Math.Sin(2 * w);
            double numR = b0 + b1 * cos1 + b2 * cos2, numI = -(b1 * sin1 + b2 * sin2);
            double denR = a0 + a1 * cos1 + a2 * cos2, denI = -(a1 * sin1 + a2 * sin2);
            double num = Math.Sqrt(numR * numR + numI * numI);
            double den = Math.Sqrt(denR * denR + denI * denI);
            return den < 1e-12 ? 1.0 : num / den;
        }
    }

    /// <summary>Compact text encoding for a band list, so the whole EQ round
    /// trips through one XML attribute alongside every other per-device audio
    /// setting rather than needing its own element shape.
    ///
    /// <para><c>PK:1050:-3.5:1.20:1|LSC:105:5.5:0.70:1</c>, one band per pipe,
    /// fields type, frequency, gain, Q, enabled. Invariant culture throughout,
    /// because a decimal comma would collide with nothing here but would still
    /// make a config unportable between machines.</para></summary>
    internal static class EqBandCodec
    {
        private static readonly (EqBandType T, string Tok)[] Toks =
        {
            (EqBandType.Peaking, "PK"), (EqBandType.LowShelf, "LSC"), (EqBandType.HighShelf, "HSC"),
            (EqBandType.HighPass, "HP"), (EqBandType.LowPass, "LP"), (EqBandType.Notch, "NO"),
        };

        public static string Encode(IReadOnlyList<EqBand> bands)
        {
            if (bands == null || bands.Count == 0) return string.Empty;
            var sb = new System.Text.StringBuilder();
            foreach (var b in bands)
            {
                if (b == null) continue;
                if (sb.Length > 0) sb.Append('|');
                sb.Append(Tok(b.Type)).Append(':')
                  .Append(b.FrequencyHz.ToString("0.##", CultureInfo.InvariantCulture)).Append(':')
                  .Append(b.GainDb.ToString("0.##", CultureInfo.InvariantCulture)).Append(':')
                  .Append(b.Q.ToString("0.###", CultureInfo.InvariantCulture)).Append(':')
                  .Append(b.Enabled ? '1' : '0');
            }
            return sb.ToString();
        }

        public static List<EqBand> Decode(string s)
        {
            var list = new List<EqBand>();
            if (string.IsNullOrWhiteSpace(s)) return list;
            foreach (var part in s.Split('|'))
            {
                var f = part.Split(':');
                if (f.Length < 4) continue;
                var t = Type(f[0]);
                if (t == null) continue;
                if (!float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float fc)) continue;
                float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float g);
                float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float q);
                list.Add(new EqBand
                {
                    Type = t.Value,
                    FrequencyHz = fc,
                    GainDb = g,
                    Q = q <= 0f ? 0.707f : q,
                    Enabled = f.Length < 5 || f[4] != "0",
                });
            }
            return list;
        }

        private static string Tok(EqBandType t)
        {
            foreach (var (tt, tok) in Toks) if (tt == t) return tok;
            return "PK";
        }

        private static EqBandType? Type(string tok)
        {
            foreach (var (tt, tk) in Toks)
                if (string.Equals(tk, tok, StringComparison.OrdinalIgnoreCase)) return tt;
            return null;
        }
    }

    /// <summary>The per-sink chain: crossfeed, then EQ, then the limiter.
    ///
    /// <para>The order is fixed and it matters. Crossfeed is a stereo-field
    /// operation and belongs on the signal before it is tone-shaped. The
    /// limiter goes last because it exists to catch what the EQ added, and it
    /// is the last thing between this chain and the Opus encoder.</para>
    ///
    /// <para>Owned by the Sink, configured off the audio thread from the
    /// per-device config, and called from SinkSource.Read after the macro,
    /// loopback and persona sources have been summed.</para></summary>
    internal sealed class MirrorChain
    {
        private readonly CrossfeedStage _cf = new();
        private readonly ParametricEqStage _eq = new();
        private readonly LimiterStage _lim = new();
        private volatile float _preamp = 1f;
        private volatile bool _any;

        public bool Active => _any;

        /// <summary>Rebuilds every stage. Off the audio thread, at the config
        /// refresh cadence, never per read.</summary>
        public void Configure(int crossfeedLevel, bool speakerPath,
                              IReadOnlyList<EqBand> bands, float preampDb,
                              bool limiterOn, float ceiling, int sampleRate,
                              float customCutHz = 700f, float customFeedDb = 4.5f)
        {
            // Crossfeed is skipped outright on the speaker paths. It would be
            // harmless there (a mono downmix stays mono through it) but it
            // would still cost four filters a sample to accomplish nothing.
            _cf.SetParams(speakerPath ? CrossfeedStage.Off : crossfeedLevel, sampleRate, customCutHz, customFeedDb);
            _eq.SetBands(bands, sampleRate);
            _lim.SetParams(limiterOn, ceiling, sampleRate);
            _preamp = preampDb == 0f ? 1f : (float)Math.Pow(10.0, Math.Clamp(preampDb, -30f, 12f) / 20.0);
            _any = _cf.Active || _eq.Active || _lim.Active || _preamp != 1f;
        }

        public void Reset()
        {
            _cf.Reset(); _eq.Reset(); _lim.Reset();
        }

        public void Process(Span<float> buf, int frames)
        {
            if (!_any || frames <= 0) return;
            float pre = _preamp;
            if (pre != 1f)
            {
                for (int i = 0, n = frames * 2; i < n; i++) buf[i] *= pre;
            }
            _cf.Process(buf, frames);
            _eq.Process(buf, frames);
            _lim.Process(buf, frames);
        }
    }

    /// <summary>Parses AutoEq's published parametric format into bands.
    ///
    /// <para>AutoEq ships settings for thousands of headphones as lines like
    /// <c>Filter 1: ON PK Fc 105 Hz Gain -3.5 dB Q 0.70</c>, plus an optional
    /// <c>Preamp: -6.0 dB</c>. The filter types map one to one onto the band
    /// types here, which is the whole reason to support the format: it turns a
    /// bank of sliders into something with an answer behind it.</para></summary>
    internal static class AutoEqProfile
    {
        private static readonly Regex FilterLine = new(
            @"^\s*Filter\s+\d+\s*:\s*(ON|OFF)\s+(\w+)\s+Fc\s+([-\d.]+)\s*Hz(?:\s+Gain\s+([-\d.]+)\s*dB)?(?:\s+Q\s+([-\d.]+))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PreampLine = new(
            @"^\s*Preamp\s*:\s*([-\d.]+)\s*dB", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Returns the parsed bands and the preamp in dB. Unknown
        /// filter types and malformed lines are skipped rather than throwing:
        /// a pasted profile is user input, and one odd line should not lose
        /// the other twenty.</summary>
        public static (List<EqBand> Bands, float PreampDb) Parse(string text)
        {
            var bands = new List<EqBand>();
            float preamp = 0f;
            if (string.IsNullOrWhiteSpace(text)) return (bands, preamp);

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                var pm = PreampLine.Match(line);
                if (pm.Success
                    && float.TryParse(pm.Groups[1].Value, NumberStyles.Float,
                                      CultureInfo.InvariantCulture, out float pre))
                {
                    preamp = pre;
                    continue;
                }

                var m = FilterLine.Match(line);
                if (!m.Success) continue;

                var type = MapType(m.Groups[2].Value);
                if (type == null) continue;

                if (!float.TryParse(m.Groups[3].Value, NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out float fc)) continue;

                float gain = 0f;
                if (m.Groups[4].Success)
                    float.TryParse(m.Groups[4].Value, NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out gain);

                float q = 0.707f;
                if (m.Groups[5].Success)
                    float.TryParse(m.Groups[5].Value, NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out q);

                bands.Add(new EqBand
                {
                    Enabled = string.Equals(m.Groups[1].Value, "ON", StringComparison.OrdinalIgnoreCase),
                    Type = type.Value,
                    FrequencyHz = fc,
                    GainDb = gain,
                    Q = q <= 0f ? 0.707f : q,
                });
            }
            return (bands, preamp);
        }

        /// <summary>AutoEq's type tokens. PK, LSC and HSC cover essentially
        /// every published profile; the rest are accepted because the format
        /// permits them.</summary>
        private static EqBandType? MapType(string tok) => tok.ToUpperInvariant() switch
        {
            "PK" or "PEQ" or "MODAL" => EqBandType.Peaking,
            "LSC" or "LS" or "LSQ" => EqBandType.LowShelf,
            "HSC" or "HS" or "HSQ" => EqBandType.HighShelf,
            "HPQ" or "HP" => EqBandType.HighPass,
            "LPQ" or "LP" => EqBandType.LowPass,
            "NO" or "NOTCH" => EqBandType.Notch,
            _ => null,
        };
    }
}
