using System;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Selective adaptive-trigger to impulse-trigger translation (#271
    /// item 3), the inverse of the AT Vibration auto-route in
    /// <see cref="Ds5EffectSynthesizer"/>. A game driving a virtual
    /// DualSense writes 11-byte trigger-effect blocks; when the physical
    /// pad is an Xbox One+ with impulse-trigger motors, the
    /// vibration-class effects translate to trigger vibration.
    ///
    /// The owner's constraint makes the split mechanical: adaptive
    /// triggers are position-dependent RESISTANCE programs first, and a
    /// resistance has no impulse equivalent, so only the modes carrying a
    /// real frequency parameter translate. Everything else renders as
    /// NOTHING, deliberately.
    ///
    /// Mode-class table and byte layouts are grounded in Nielk1's
    /// TriggerEffectGenerator (cloned reference:
    /// DualSenseY-v2/thirdparty/duaLib/src/include/triggerFactory.h:36-59
    /// and src/source/triggerFactory.cpp), cross-checked against this
    /// repo's own encoder (Ds5EffectSynthesizer.EncodeTrigger):
    ///
    ///   Vibration-class (translate):
    ///     0x06 Simple_Vibration  [freq@1, amplitude@2 (0-255), startPos@3 (0-255)]
    ///     0x23 Galloping         [zones@1-2, feetRatio@3, freq@4]
    ///     0x26 Vibration         [activeZones@1-2, amp 3-bit x10 @3-6, freq@9]
    ///     0x27 Machine           [zones@1-2, ampA|ampB 3-bit @3, freq@4, period@5]
    ///
    ///   Resistance-class (ignore): 0x01 Simple_Feedback,
    ///     0x02 Simple_Weapon, 0x11/0x12 Limited, 0x21 Feedback,
    ///     0x22 Bow, 0x25 Weapon. Off: 0x00 / 0x05.
    /// </summary>
    internal static class AtToImpulseTranslator
    {
        internal const byte ModeSimpleVibration = 0x06;
        internal const byte ModeGalloping = 0x23;
        internal const byte ModeVibration = 0x26;
        internal const byte ModeMachine = 0x27;

        /// <summary>True for the DS5 trigger-effect modes that carry a
        /// real frequency parameter. Everything else is resistance or
        /// off and must translate to nothing.</summary>
        internal static bool IsVibrationClass(byte mode) =>
            mode == ModeSimpleVibration
            || mode == ModeGalloping
            || mode == ModeVibration
            || mode == ModeMachine;

        // Above this frequency an ERM impulse motor can't articulate
        // discrete pulses, so the envelope renders continuous instead of
        // aliasing against the poll clock.
        private const int ContinuousFrequencyHz = 50;

        /// <summary>
        /// Evaluates one latched 11-byte trigger-effect block into an
        /// impulse-motor magnitude. Position-honest: the DS5 firmware
        /// only vibrates while the trigger sits in the effect's active
        /// region, so the translation gates on the slot's output trigger
        /// position the same way. Pure; the clock arrives as a
        /// parameter so tests are deterministic.
        /// </summary>
        /// <param name="block">11-byte effect block (mode + 10 params).</param>
        /// <param name="triggerPos">Slot output trigger position, 0-255.</param>
        /// <param name="nowMs">Millisecond clock for the pulse envelope.</param>
        internal static ushort Evaluate(ReadOnlySpan<byte> block, byte triggerPos, long nowMs)
        {
            if (block.Length < 11) return 0;
            byte mode = block[0];
            if (!IsVibrationClass(mode)) return 0;

            // 10 zones map linearly across the trigger throw (this repo's
            // EncodeTrigger zone comment: zone = floor(pos / 25.6)).
            int zone = Math.Min(triggerPos * 10 / 256, 9);

            switch (mode)
            {
                case ModeSimpleVibration:
                {
                    byte freq = block[1];
                    byte amplitude = block[2];
                    byte startPos = block[3];
                    if (freq == 0 || amplitude == 0) return 0;
                    if (triggerPos < startPos) return 0;
                    double amp01 = amplitude / 255.0;
                    return Envelope(amp01, freq, nowMs, dutyNum: 1, dutyDen: 2);
                }

                case ModeVibration:
                {
                    // activeZones bitmap at 1-2, packed 3-bit amplitudes
                    // at 3-6, frequency at 9 (triggerFactory.cpp
                    // Vibration builder). Wire amplitude a encodes user
                    // strength a+1 of 8.
                    byte freq = block[9];
                    if (freq == 0) return 0;
                    int activeZones = block[1] | (block[2] << 8);
                    if ((activeZones & (1 << zone)) == 0) return 0;
                    uint ampZones = (uint)(block[3] | (block[4] << 8) | (block[5] << 16) | (block[6] << 24));
                    int amp3 = (int)((ampZones >> (3 * zone)) & 0x07);
                    double amp01 = (amp3 + 1) / 8.0;
                    return Envelope(amp01, freq, nowMs, dutyNum: 1, dutyDen: 2);
                }

                case ModeGalloping:
                {
                    // Start/end zone bitmap at 1-2, frequency at 4. The
                    // feet-ratio byte at 3 shapes WHEN the two hoof ticks
                    // land inside each cycle, not how hard; render as a
                    // quarter-duty full-strength pulse train.
                    byte freq = block[4];
                    if (freq == 0) return 0;
                    if (!ZoneInsideStartStop(block[1], block[2], zone)) return 0;
                    return Envelope(1.0, freq, nowMs, dutyNum: 1, dutyDen: 4);
                }

                case ModeMachine:
                {
                    // Start/end zone bitmap at 1-2, amplitudes A and B as
                    // a packed 3-bit pair at 3, frequency at 4, period at
                    // 5 (triggerFactory.cpp Machine builder). Render the
                    // two amplitudes alternating at the effect frequency;
                    // the period byte's exact firmware semantics aren't
                    // documented in the references, so it is ignored here.
                    byte freq = block[4];
                    if (freq == 0) return 0;
                    if (!ZoneInsideStartStop(block[1], block[2], zone)) return 0;
                    int ampA = block[3] & 0x07;
                    int ampB = (block[3] >> 3) & 0x07;
                    int periodMs = PulsePeriodMs(freq);
                    if (periodMs == 0)
                    {
                        double avg01 = ((ampA + 1) + (ampB + 1)) / 16.0;
                        return (ushort)Math.Clamp((int)(avg01 * 65535), 0, 65535);
                    }
                    bool firstHalf = (nowMs % (2L * periodMs)) < periodMs;
                    double amp01 = ((firstHalf ? ampA : ampB) + 1) / 8.0;
                    return (ushort)Math.Clamp((int)(amp01 * 65535), 0, 65535);
                }
            }
            return 0;
        }

        /// <summary>Start/stop encoding used by Galloping and Machine:
        /// exactly two set bits mark the start and end zones
        /// (triggerFactory.cpp: <c>(1 &lt;&lt; start) | (1 &lt;&lt; end)</c>).
        /// Active while the trigger sits between them, inclusive.</summary>
        private static bool ZoneInsideStartStop(byte zonesLo, byte zonesHi, int zone)
        {
            int zones = zonesLo | (zonesHi << 8);
            if (zones == 0) return false;
            int start = System.Numerics.BitOperations.TrailingZeroCount(zones);
            int end = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)zones);
            return zone >= start && zone <= end;
        }

        /// <summary>0 means "render continuous" (frequency too high for
        /// the motor to articulate pulses).</summary>
        private static int PulsePeriodMs(byte freq)
            => freq >= ContinuousFrequencyHz ? 0 : 1000 / Math.Max((int)freq, 1);

        private static ushort Envelope(double amp01, byte freq, long nowMs, int dutyNum, int dutyDen)
        {
            int periodMs = PulsePeriodMs(freq);
            if (periodMs > 0)
            {
                long phase = nowMs % periodMs;
                if (phase >= (long)periodMs * dutyNum / dutyDen) return 0;
            }
            return (ushort)Math.Clamp((int)(amp01 * 65535), 0, 65535);
        }
    }
}
