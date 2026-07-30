using System;
using System.IO;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Haptics;
using PadForge.Engine.Touchpad;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #219 swipe-haptic ticks: the travel-detent evaluator
    /// (semantics per SteamlessController ControllerManager.cpp:316-395),
    /// the one-shot pulse encoders (DS4MapperTest's 0x8F feedback packet,
    /// SteamlessController's 0x82 tick command), the Sony-side pulse
    /// cells (DS4MapperTest's 80 ms burst, max-combined with game rumble
    /// like audio-bass), and the settings round-trip legs.
    /// </summary>
    public class SwipeHapticsTests
    {
        // ─── Evaluator helpers ────────────────────────────────────────

        private static TouchpadInputState Pad(int fingers = 2) => new TouchpadInputState(fingers);

        private static void SetFinger(TouchpadInputState pad, int slot, bool down, float x, float y, int contactId)
        {
            pad.FingerDown[slot] = down;
            pad.FingerX[slot] = x;
            pad.FingerY[slot] = y;
            pad.FingerContactId[slot] = down ? contactId : -1;
        }

        private const float Tick = SwipeHapticsEvaluator.DefaultTickDistance; // 5000/65536 ≈ 0.0763

        // ─── Evaluator: seed / travel / ticks ─────────────────────────

        [Fact]
        public void NewTouch_DoesNotTick()
        {
            var st = new SwipeHapticsState();
            var pad = Pad();
            SetFinger(pad, 0, true, 0.5f, 0.5f, 1);
            Assert.Equal(0, SwipeHapticsEvaluator.Update(st, pad));
        }

        [Fact]
        public void TravelPastThreshold_TicksOnce()
        {
            var st = new SwipeHapticsState();
            var pad = Pad();
            SetFinger(pad, 0, true, 0.10f, 0.5f, 1);
            SwipeHapticsEvaluator.Update(st, pad); // seed

            // Move just under the threshold: no tick yet.
            SetFinger(pad, 0, true, 0.10f + Tick * 0.9f, 0.5f, 1);
            Assert.Equal(0, SwipeHapticsEvaluator.Update(st, pad));

            // Cross it: one tick, remainder carried.
            SetFinger(pad, 0, true, 0.10f + Tick * 1.2f, 0.5f, 1);
            Assert.Equal(1, SwipeHapticsEvaluator.Update(st, pad));
        }

        [Fact]
        public void LongSweep_EmitsMultipleTicksInOneFrame()
        {
            // SteamlessController's while-loop (:371-374) can emit several
            // ticks in one report when the travel spans multiple detents.
            var st = new SwipeHapticsState();
            var pad = Pad();
            SetFinger(pad, 0, true, 0.05f, 0.5f, 1);
            SwipeHapticsEvaluator.Update(st, pad); // seed

            SetFinger(pad, 0, true, 0.05f + Tick * 3.5f, 0.5f, 1);
            Assert.Equal(3, SwipeHapticsEvaluator.Update(st, pad));

            // The 0.5 remainder still counts toward the next detent.
            SetFinger(pad, 0, true, 0.05f + Tick * 4.1f, 0.5f, 1);
            Assert.Equal(1, SwipeHapticsEvaluator.Update(st, pad));
        }

        [Fact]
        public void DiagonalTravel_IsEuclidean()
        {
            var st = new SwipeHapticsState();
            var pad = Pad();
            SetFinger(pad, 0, true, 0.2f, 0.2f, 1);
            SwipeHapticsEvaluator.Update(st, pad); // seed

            // dx = dy = Tick / sqrt(2) => Euclidean distance exactly one detent.
            float leg = Tick / MathF.Sqrt(2f) * 1.01f;
            SetFinger(pad, 0, true, 0.2f + leg, 0.2f + leg, 1);
            Assert.Equal(1, SwipeHapticsEvaluator.Update(st, pad));
        }

        [Fact]
        public void Lift_ResetsAccumulator()
        {
            var st = new SwipeHapticsState();
            var pad = Pad();
            SetFinger(pad, 0, true, 0.10f, 0.5f, 1);
            SwipeHapticsEvaluator.Update(st, pad); // seed
            SetFinger(pad, 0, true, 0.10f + Tick * 0.8f, 0.5f, 1);
            Assert.Equal(0, SwipeHapticsEvaluator.Update(st, pad)); // 80% banked

            // Lift, then a new touch continues from a fresh seed: the
            // banked 80% must NOT combine with the next 30% into a tick.
            SetFinger(pad, 0, false, 0f, 0f, -1);
            SwipeHapticsEvaluator.Update(st, pad);
            SetFinger(pad, 0, true, 0.5f, 0.5f, 2);
            SwipeHapticsEvaluator.Update(st, pad); // seed (no tick on the jump)
            SetFinger(pad, 0, true, 0.5f + Tick * 0.3f, 0.5f, 2);
            Assert.Equal(0, SwipeHapticsEvaluator.Update(st, pad));
        }

        [Fact]
        public void NewContactId_SameSlot_ReseedsInsteadOfTickingOnTheJump()
        {
            var st = new SwipeHapticsState();
            var pad = Pad();
            SetFinger(pad, 0, true, 0.1f, 0.1f, 1);
            SwipeHapticsEvaluator.Update(st, pad);

            // Same slot, new contact ID landing far away (finger lifted and
            // a new one landed between polls): the positional jump must not
            // register as travel.
            SetFinger(pad, 0, true, 0.9f, 0.9f, 2);
            Assert.Equal(0, SwipeHapticsEvaluator.Update(st, pad));
        }

        [Fact]
        public void Click_SuppressesTicks_AndReseeds()
        {
            var st = new SwipeHapticsState();
            var pad = Pad();
            SetFinger(pad, 0, true, 0.10f, 0.5f, 1);
            SwipeHapticsEvaluator.Update(st, pad); // seed

            // Clicked travel: no ticks, accumulator reseeds along the way
            // (SteamlessController's !clicked gate + click-release reseed).
            pad.Clicked = true;
            SetFinger(pad, 0, true, 0.10f + Tick * 2f, 0.5f, 1);
            Assert.Equal(0, SwipeHapticsEvaluator.Update(st, pad));

            // Release the click: travel counts from the release point.
            pad.Clicked = false;
            SetFinger(pad, 0, true, 0.10f + Tick * 2.5f, 0.5f, 1);
            Assert.Equal(0, SwipeHapticsEvaluator.Update(st, pad)); // only 0.5 since release
            SetFinger(pad, 0, true, 0.10f + Tick * 3.2f, 0.5f, 1);
            Assert.Equal(1, SwipeHapticsEvaluator.Update(st, pad));
        }

        [Fact]
        public void TwoFingers_AccumulateIndependently()
        {
            var st = new SwipeHapticsState();
            var pad = Pad(2);
            SetFinger(pad, 0, true, 0.1f, 0.5f, 1);
            SetFinger(pad, 1, true, 0.9f, 0.5f, 2);
            SwipeHapticsEvaluator.Update(st, pad); // seed both

            // Each finger travels 60% of a detent: neither alone crosses,
            // and their travel must not pool into a shared tick.
            SetFinger(pad, 0, true, 0.1f + Tick * 0.6f, 0.5f, 1);
            SetFinger(pad, 1, true, 0.9f - Tick * 0.6f, 0.5f, 2);
            Assert.Equal(0, SwipeHapticsEvaluator.Update(st, pad));

            // Both cross on the same frame: two ticks.
            SetFinger(pad, 0, true, 0.1f + Tick * 1.2f, 0.5f, 1);
            SetFinger(pad, 1, true, 0.9f - Tick * 1.2f, 0.5f, 2);
            Assert.Equal(2, SwipeHapticsEvaluator.Update(st, pad));
        }

        [Fact]
        public void SeparateStates_AreIsolated()
        {
            // Two (slot, device, pad) keys hold independent accumulators:
            // banked travel in one never ticks the other.
            var a = new SwipeHapticsState();
            var b = new SwipeHapticsState();
            var pad = Pad();
            SetFinger(pad, 0, true, 0.1f, 0.5f, 1);
            SwipeHapticsEvaluator.Update(a, pad);
            SetFinger(pad, 0, true, 0.1f + Tick * 0.9f, 0.5f, 1);
            SwipeHapticsEvaluator.Update(a, pad); // a banks 90%

            SetFinger(pad, 0, true, 0.1f + Tick * 0.9f, 0.5f, 1);
            SwipeHapticsEvaluator.Update(b, pad); // b seeds here
            SetFinger(pad, 0, true, 0.1f + Tick * 1.1f, 0.5f, 1);
            Assert.Equal(0, SwipeHapticsEvaluator.Update(b, pad)); // b has only 20%
        }

        // ─── Encoders ─────────────────────────────────────────────────

        [Fact]
        public void SteamClassicPulse_MatchesDs4MapperTestPacket()
        {
            // DS4MapperTest SteamControllerDevice.cs:404-419: 0x8F, 0x07,
            // position, amplitude u16 LE, period u16 LE, count u16 LE.
            var blob = HapticToneEncoder.EncodeSteamClassicPulse(
                HapticToneEncoder.SteamPulsePadLeft, 1200, 600, 1);
            Assert.Equal(64, blob.Length);
            Assert.Equal(0x8F, blob[0]);
            Assert.Equal(0x07, blob[1]);
            Assert.Equal(0x01, blob[2]);            // left pad
            Assert.Equal(1200 & 0xFF, blob[3]);     // 0xB0
            Assert.Equal(1200 >> 8, blob[4]);       // 0x04
            Assert.Equal(600 & 0xFF, blob[5]);      // 0x58
            Assert.Equal(600 >> 8, blob[6]);        // 0x02
            Assert.Equal(1, blob[7]);
            Assert.Equal(0, blob[8]);
            for (int i = 9; i < 64; i++) Assert.Equal(0, blob[i]);

            var right = HapticToneEncoder.EncodeSteamClassicPulse(
                HapticToneEncoder.SteamPulsePadRight, 200, 600, 1);
            Assert.Equal(0x00, right[2]);           // right pad
        }

        [Theory]
        [InlineData(1.0f, 1200)]  // (1200-200)*1 + 200
        [InlineData(0.5f, 700)]
        [InlineData(0.0f, 200)]
        [InlineData(2.0f, 1200)]  // clamped
        public void SteamPulseOnTime_MatchesDs4MapperTestFormula(float amp, int expectedUs)
        {
            Assert.Equal(expectedUs, HapticToneEncoder.SteamPulseOnTimeUs(amp));
        }

        [Fact]
        public void TritonTickCommand_MatchesSteamlessControllerBytes()
        {
            // SteamlessController SteamController.cpp:320-331: [0x82, side,
            // command, gainDb], command 1 = tick, move gain -50 dB.
            var left = HapticToneEncoder.EncodeTritonTickCommand(0x01, -50);
            Assert.Equal(new byte[] { 0x82, 0x01, 0x01, unchecked((byte)(sbyte)(-50)) }, left);

            var both = HapticToneEncoder.EncodeTritonTickCommand(0x03, -60);
            Assert.Equal(0x03, both[1]);
            Assert.Equal(unchecked((byte)(sbyte)(-60)), both[3]);
        }

        [Fact]
        public void TritonTickGain_ReferenceLevelAtFullIntensity()
        {
            Assert.Equal(HapticToneEncoder.TritonTickBaseGainDb,
                HapticToneEncoder.TritonTickGainDb(1f)); // -50, the reference constant
            Assert.Equal(-56, HapticToneEncoder.TritonTickGainDb(0.5f)); // -50 + 20*log10(0.5)
            Assert.Equal(-70, HapticToneEncoder.TritonTickGainDb(0.1f));
            Assert.Equal(-128, HapticToneEncoder.TritonTickGainDb(0f)); // silent
        }

        // ─── Sony pulse cells + dispatcher mix ────────────────────────

        [Fact]
        public void PulseCell_HoldsFor80ms_ThenExpires()
        {
            TouchpadPulseService.Clear();
            var dev = Guid.NewGuid();
            TouchpadPulseService.Pulse(3, dev, 0.5f, nowMs: 1000);

            Assert.Equal(0.5f, TouchpadPulseService.CurrentLevel(3, dev, 1000));
            Assert.Equal(0.5f, TouchpadPulseService.CurrentLevel(3, dev, 1079));
            Assert.Equal(0f, TouchpadPulseService.CurrentLevel(3, dev, 1080)); // 80 ms window closed
        }

        [Fact]
        public void PulseCell_MaxCombines_WhileLive()
        {
            // DS4MapperTest's pending-haptics merge: a stronger request wins,
            // a weaker one never lowers a live burst.
            TouchpadPulseService.Clear();
            var dev = Guid.NewGuid();
            TouchpadPulseService.Pulse(0, dev, 0.3f, nowMs: 1000);
            TouchpadPulseService.Pulse(0, dev, 0.8f, nowMs: 1010);
            Assert.Equal(0.8f, TouchpadPulseService.CurrentLevel(0, dev, 1020));

            TouchpadPulseService.Pulse(0, dev, 0.3f, nowMs: 1030);
            Assert.Equal(0.8f, TouchpadPulseService.CurrentLevel(0, dev, 1040));

            // After the burst dies, a weaker fresh tick replaces it outright.
            TouchpadPulseService.Pulse(0, dev, 0.3f, nowMs: 5000);
            Assert.Equal(0.3f, TouchpadPulseService.CurrentLevel(0, dev, 5010));
        }

        [Fact]
        public void PulseCells_ArePerSlotAndPerDevice()
        {
            // TATTOO: config and runtime state key per (slot, device). One
            // device's burst must not leak to another slot or device.
            TouchpadPulseService.Clear();
            var devA = Guid.NewGuid();
            var devB = Guid.NewGuid();
            TouchpadPulseService.Pulse(1, devA, 0.7f, nowMs: 1000);

            Assert.Equal(0.7f, TouchpadPulseService.CurrentLevel(1, devA, 1010));
            Assert.Equal(0f, TouchpadPulseService.CurrentLevel(2, devA, 1010)); // other slot
            Assert.Equal(0f, TouchpadPulseService.CurrentLevel(1, devB, 1010)); // other device
        }

        [Fact]
        public void SlotActive_TracksLiveBursts_ForTheDispatcherKeepalive()
        {
            TouchpadPulseService.Clear();
            var dev = Guid.NewGuid();
            Assert.False(TouchpadPulseService.IsSlotActive(4, 1000));
            TouchpadPulseService.Pulse(4, dev, 0.5f, nowMs: 1000);
            Assert.True(TouchpadPulseService.IsSlotActive(4, 1050));
            Assert.False(TouchpadPulseService.IsSlotActive(4, 1200)); // expired
            Assert.False(TouchpadPulseService.IsSlotActive(5, 1050)); // other slot
        }

        [Fact]
        public void MixIntoMotors_IsMaxLikeAudioBass()
        {
            // Pulse stronger than game rumble: pulse wins on both motors.
            ushort l = 10000, r = 20000;
            TouchpadPulseService.MixIntoMotors(ref l, ref r, 0.5f);
            Assert.Equal((ushort)32767, l);
            Assert.Equal((ushort)32767, r);

            // Live game rumble stronger than the pulse: rumble untouched.
            l = 60000; r = 61000;
            TouchpadPulseService.MixIntoMotors(ref l, ref r, 0.5f);
            Assert.Equal((ushort)60000, l);
            Assert.Equal((ushort)61000, r);

            // Zero level: no-op.
            l = 123; r = 456;
            TouchpadPulseService.MixIntoMotors(ref l, ref r, 0f);
            Assert.Equal((ushort)123, l);
            Assert.Equal((ushort)456, r);
        }

        // ─── Settings legs ────────────────────────────────────────────

        [Fact]
        public void Defaults_AreOff_AtMediumIntensity()
        {
            var s = TouchpadGestureSettings.Default();
            Assert.False(s.EnableSwipeHaptics);
            Assert.Equal(0.5f, s.SwipeHapticsIntensity);
        }

        [Fact]
        public void Clone_CopiesSwipeHapticsFields()
        {
            var s = new TouchpadGestureSettings
            {
                EnableSwipeHaptics = true,
                SwipeHapticsIntensity = 0.8f,
            };
            var c = s.Clone();
            Assert.True(c.EnableSwipeHaptics);
            Assert.Equal(0.8f, c.SwipeHapticsIntensity);
        }

        [Fact]
        public void PadSettingXml_RoundTripsSwipeHapticsFields()
        {
            var ps = new PadSetting
            {
                TouchpadSettings = new[]
                {
                    new TouchpadSettingsEntry
                    {
                        DeviceGuid = "11111111-2222-3333-4444-555555555555",
                        TouchpadIndex = 1,
                        Settings = new TouchpadGestureSettings
                        {
                            EnableSwipeHaptics = true,
                            SwipeHapticsIntensity = 0.3f,
                        },
                    },
                },
            };

            var ser = new XmlSerializer(typeof(PadSetting));
            using var sw = new StringWriter();
            ser.Serialize(sw, ps);
            using var sr = new StringReader(sw.ToString());
            var back = (PadSetting)ser.Deserialize(sr);

            var entry = Assert.Single(back.TouchpadSettings);
            Assert.Equal(1, entry.TouchpadIndex);
            Assert.True(entry.Settings.EnableSwipeHaptics);
            Assert.Equal(0.3f, entry.Settings.SwipeHapticsIntensity);
        }

        [Fact]
        public void ContentHash_ReflectsSwipeHapticsFields()
        {
            // The SaveToFile dedup-by-checksum trap: two PadSettings that
            // differ only in a per-pad toggle must hash differently or the
            // second one's toggle silently drops on save.
            static PadSetting Make(bool enabled, float intensity) => new PadSetting
            {
                TouchpadSettings = new[]
                {
                    new TouchpadSettingsEntry
                    {
                        DeviceGuid = "11111111-2222-3333-4444-555555555555",
                        TouchpadIndex = 0,
                        Settings = new TouchpadGestureSettings
                        {
                            EnableSwipeHaptics = enabled,
                            SwipeHapticsIntensity = intensity,
                        },
                    },
                },
            };

            string baseline = Make(false, 0.5f).ComputeChecksum();
            Assert.NotEqual(baseline, Make(true, 0.5f).ComputeChecksum());
            Assert.NotEqual(baseline, Make(false, 0.9f).ComputeChecksum());
            Assert.Equal(baseline, Make(false, 0.5f).ComputeChecksum());
        }
    }
}
