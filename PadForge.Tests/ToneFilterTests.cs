using PadForge.Common.Input;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    /// <summary>Covers the #202 high-tone filter: the Fold/Cut transform the
    /// haptic-tone stream loop applies to every reduced (pitch, amplitude)
    /// pair, Cut's re-arm guard for the pitch-only 0x8F families, and the
    /// config's serialization sibling set.</summary>
    public class ToneFilterTests
    {
        // ── The transform (HapticToneService.ApplyToneFilter) ──

        [Fact]
        public void Off_Is_Identity_Even_Above_Limit()
        {
            float last = 0f; bool latch = false;
            var (hz, amp) = HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterOff, 800, 1300f, 0.7f, ref last, ref latch);
            Assert.Equal(1300f, hz);
            Assert.Equal(0.7f, amp);
        }

        [Fact]
        public void Fold_Halves_Into_The_Pass_Band()
        {
            // The recipe's canonical case: 1300 Hz with an 800 Hz limit
            // folds one octave to 650, pitch class preserved.
            float last = 0f; bool latch = false;
            var (hz, amp) = HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterFold, 800, 1300f, 0.7f, ref last, ref latch);
            Assert.Equal(650f, hz);
            Assert.Equal(0.7f, amp);
        }

        [Fact]
        public void Fold_Repeats_Until_Below_A_Low_Limit()
        {
            // 1300 with the minimum 100 Hz limit: four halvings, landing in
            // (limit/2, limit] like FoldJoyConFrequency's band fold.
            float last = 0f; bool latch = false;
            var (hz, _) = HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterFold, 100, 1300f, 0.7f, ref last, ref latch);
            Assert.Equal(81.25f, hz);
        }

        [Fact]
        public void Fold_Leaves_The_Pass_Band_Alone()
        {
            float last = 0f; bool latch = false;
            var (hz, amp) = HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterFold, 800, 200f, 0.5f, ref last, ref latch);
            Assert.Equal(200f, hz);
            Assert.Equal(0.5f, amp);
            Assert.Equal(200f, last);
        }

        [Fact]
        public void Cut_Zeroes_Amp_And_Holds_The_Last_Passed_Pitch()
        {
            // An engine tone passes, then a beep is cut: the cut tick must
            // re-emit the ENGINE pitch, because the SC 2015 / Deck 0x8F
            // square never reads amp and re-arms on any pitch shift while
            // other audio keeps the stream alive. Emitting the beep pitch
            // would fire the full-strength square at the very frequency
            // the user asked to remove.
            float last = 0f; bool latch = false;
            HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterCut, 800, 200f, 0.6f, ref last, ref latch);
            Assert.Equal(200f, last);

            var (hz, amp) = HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterCut, 800, 1200f, 0.9f, ref last, ref latch);
            Assert.Equal(200f, hz);
            Assert.Equal(0f, amp);
            // A cut tick must not overwrite the held pitch.
            Assert.Equal(200f, last);
        }

        [Fact]
        public void Cut_With_No_Prior_Content_Emits_Note_Stop_Shape()
        {
            // First-ever tick is already above the limit: the held pitch is
            // 0, which EncodeSteamClassic treats as the NOTE_STOP blob.
            float last = 0f; bool latch = false;
            var (hz, amp) = HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterCut, 800, 1200f, 0.9f, ref last, ref latch);
            Assert.Equal(0f, hz);
            Assert.Equal(0f, amp);
        }

        [Fact]
        public void Limit_Is_Clamped_To_The_Reducer_Band()
        {
            // A hand-edited 0 Hz limit clamps to 100 instead of folding
            // everything to the floor (or spinning).
            float last = 0f; bool latch = false;
            var (hz, _) = HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterFold, 0, 150f, 0.5f, ref last, ref latch);
            Assert.Equal(75f, hz);
            var (hz2, _) = HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterFold, 0, 90f, 0.5f, ref last, ref latch);
            Assert.Equal(90f, hz2);
        }

        [Fact]
        public void Non_Finite_Pitch_Skips_The_Filter_And_Terminates()
        {
            // Same guard as FoldJoyConFrequency: +Inf would halve forever.
            float last = 50f; bool latch = false;
            var (hz, amp) = HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterFold, 800, float.PositiveInfinity, 0.5f, ref last, ref latch);
            Assert.Equal(float.PositiveInfinity, hz);
            Assert.Equal(0.5f, amp);
        }

        [Fact]
        public void Silence_Does_Not_Update_The_Held_Pitch()
        {
            float last = 300f; bool latch = false;
            HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterCut, 800, 0f, 0f, ref last, ref latch);
            Assert.Equal(300f, last);
        }

        // ── Boundary hysteresis (the 842 Hz flap) ──

        [Fact]
        public void Boundary_Tie_Folds_Both_Members_Instead_Of_Flapping()
        {
            // Owner-observed on SC 2026 hardware (2026-07-10): a sine at
            // 842-843 Hz with an 800 Hz limit flapped wildly. The reducer
            // quantizes pitch to 8000/lag, so that tone is measured as
            // either 888.9 (lag 9) or 800.0 (lag 10) tick to tick. Without
            // hysteresis the outputs alternate 444.4 / 800.0, an octave.
            // With the latch, both members fold: 444.4 / 400.0, one grid
            // step of warble.
            float last = 0f; bool latch = false;
            var (hz1, _) = HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterFold, 800, 8000f / 9f, 0.6f, ref last, ref latch);
            Assert.Equal(8000f / 18f, hz1);
            Assert.True(latch);

            var (hz2, _) = HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterFold, 800, 800f, 0.6f, ref last, ref latch);
            Assert.Equal(400f, hz2);
            Assert.True(latch);

            // And back to the upper member: still folded, still latched.
            var (hz3, _) = HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterFold, 800, 8000f / 9f, 0.6f, ref last, ref latch);
            Assert.Equal(8000f / 18f, hz3);
        }

        [Fact]
        public void Latch_Releases_One_Grid_Step_Below_The_Limit()
        {
            // Content genuinely dropping out of the boundary region measures
            // at the next grid pitch down (727.3 for an 800 limit) and must
            // play unfolded with the latch cleared.
            float last = 0f; bool latch = false;
            HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterFold, 800, 900f, 0.6f, ref last, ref latch);
            Assert.True(latch);

            var (hz, _) = HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterFold, 800, 8000f / 11f, 0.6f, ref last, ref latch);
            Assert.Equal(8000f / 11f, hz);
            Assert.False(latch);
        }

        [Fact]
        public void Unlatched_Tie_Member_Plays_Unfolded()
        {
            // Fresh content at the tie's lower member (no prior above-limit
            // tick) is in-band and must not fold: the latch only extends an
            // existing engagement, it never creates one.
            float last = 0f; bool latch = false;
            var (hz, _) = HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterFold, 800, 800f, 0.6f, ref last, ref latch);
            Assert.Equal(800f, hz);
            Assert.False(latch);
        }

        [Fact]
        public void Cut_Boundary_Tie_Stays_Silent_Instead_Of_Stuttering()
        {
            float last = 0f; bool latch = false;
            HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterCut, 800, 300f, 0.6f, ref last, ref latch);

            var (_, a1) = HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterCut, 800, 8000f / 9f, 0.6f, ref last, ref latch);
            var (hz2, a2) = HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterCut, 800, 800f, 0.6f, ref last, ref latch);
            Assert.Equal(0f, a1);
            Assert.Equal(0f, a2);
            Assert.Equal(300f, hz2);
        }

        [Fact]
        public void Silence_Resets_The_Latch()
        {
            float last = 0f; bool latch = false;
            HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterFold, 800, 900f, 0.6f, ref last, ref latch);
            Assert.True(latch);

            // A silent tick between contents clears the engagement...
            HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterFold, 800, 0f, 0f, ref last, ref latch);
            Assert.False(latch);

            // ...so the next content at the tie's lower member is fresh.
            var (hz, _) = HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterFold, 800, 800f, 0.6f, ref last, ref latch);
            Assert.Equal(800f, hz);
        }

        [Fact]
        public void Silent_Held_Pitch_Above_Limit_Still_Folds()
        {
            // The reducer holds its last pitch at zero loudness. The fold
            // must still apply on those ticks: the 0x8F square is pitch-only
            // and would re-arm at the unfolded pitch during the hangover
            // window otherwise.
            float last = 0f; bool latch = false;
            var (hz, amp) = HapticToneService.ApplyToneFilter(
                HapticToneService.ToneFilterFold, 800, 1000f, 0f, ref last, ref latch);
            Assert.Equal(500f, hz);
            Assert.Equal(0f, amp);
            // Engagement follows the pitch, not the loudness: a held pitch
            // above the limit keeps the latch up through the quiet gap.
            // Only true silence (pitch 0, the stream loop's peak-gate shape)
            // clears it, as Silence_Resets_The_Latch pins.
            Assert.True(latch);
        }

        // ── Serialization sibling set ──

        [Fact]
        public void Apply_Maps_The_Fields_And_Coalesces_Null_Mode()
        {
            var cfg = new DeviceSlotConfig();
            SettingsService.ApplyDeviceSlotConfigData(cfg, new DeviceSlotConfigData
            {
                AudioToneFilterMode = "Fold",
                AudioToneLimitHz = 650,
            });
            Assert.Equal("Fold", cfg.AudioToneFilterMode);
            Assert.Equal(650, cfg.AudioToneLimitHz);

            // Old copy payloads carry null: coalesce to Off, never null.
            SettingsService.ApplyDeviceSlotConfigData(cfg, new DeviceSlotConfigData
            {
                AudioToneFilterMode = null,
            });
            Assert.Equal("Off", cfg.AudioToneFilterMode);
        }

        [Fact]
        public void Legacy_Xml_Without_The_Attributes_Loads_As_Off_800()
        {
            var ser = new System.Xml.Serialization.XmlSerializer(typeof(DeviceSlotConfigData));
            var legacy = "<DeviceSlotConfigData SlotIndex=\"0\" AudioPassthroughEnabled=\"true\" />";
            using var reader = new System.IO.StringReader(legacy);
            var data = (DeviceSlotConfigData)ser.Deserialize(reader);
            Assert.Equal("Off", data.AudioToneFilterMode);
            Assert.Equal(800, data.AudioToneLimitHz);
        }

        [Fact]
        public void Xml_Round_Trips_A_Configured_Filter()
        {
            var ser = new System.Xml.Serialization.XmlSerializer(typeof(DeviceSlotConfigData));
            var sw = new System.IO.StringWriter();
            ser.Serialize(sw, new DeviceSlotConfigData
            {
                AudioToneFilterMode = "Cut",
                AudioToneLimitHz = 500,
            });
            using var reader = new System.IO.StringReader(sw.ToString());
            var back = (DeviceSlotConfigData)ser.Deserialize(reader);
            Assert.Equal("Cut", back.AudioToneFilterMode);
            Assert.Equal(500, back.AudioToneLimitHz);
        }

        [Fact]
        public void Configured_Predicates_Keep_A_Chosen_Filter_Copy_Worthy()
        {
            // The #185 lesson: a non-default setting must keep the config
            // alive for Copy / Paste / Copy From even with passthrough off.
            Assert.True(SettingsService.IsDeviceSlotConfigDataConfigured(
                new DeviceSlotConfigData { AudioToneFilterMode = "Fold" }));
            Assert.False(SettingsService.IsDeviceSlotConfigDataConfigured(
                new DeviceSlotConfigData { AudioToneFilterMode = "Off" }));

            Assert.True(SettingsService.IsDeviceConfigConfigured(
                new DeviceSlotConfig { AudioToneFilterMode = "Cut" }));
            Assert.False(SettingsService.IsDeviceConfigConfigured(
                new DeviceSlotConfig()));
        }
    }
}
