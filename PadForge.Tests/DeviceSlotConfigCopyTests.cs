using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    /// <summary>Covers the "is this PlayStation config configured?" check that decides
    /// which controller's Lighting / Adaptive Trigger / Audio config a Copy / Paste /
    /// Copy From uses as the fallback. The old check ignored audio, so an audio-only
    /// setup read as empty and was skipped.</summary>
    public class DeviceSlotConfigCopyTests
    {
        [Fact]
        public void Configured_FalseForAllDefault()
        {
            Assert.False(SettingsService.IsDeviceSlotConfigDataConfigured(new DeviceSlotConfigData()));
            Assert.False(SettingsService.IsDeviceSlotConfigDataConfigured(null));
        }

        [Fact]
        public void Configured_TrueForAudioOnly()
        {
            Assert.True(SettingsService.IsDeviceSlotConfigDataConfigured(
                new DeviceSlotConfigData { AudioPassthroughEnabled = true }));
            Assert.True(SettingsService.IsDeviceSlotConfigDataConfigured(
                new DeviceSlotConfigData { AudioLightbarEnabled = true }));
            Assert.True(SettingsService.IsDeviceSlotConfigDataConfigured(
                new DeviceSlotConfigData { AudioMirrorSourceId = "some-output" }));
        }

        [Fact]
        public void Configured_TrueForLightingOrTriggers()
        {
            Assert.True(SettingsService.IsDeviceSlotConfigDataConfigured(
                new DeviceSlotConfigData { LightbarMode = LightbarMode.Static }));
            Assert.True(SettingsService.IsDeviceSlotConfigDataConfigured(
                new DeviceSlotConfigData { LeftTriggerMode = AdaptiveTriggerMode.Weapon }));
        }

        /// <summary>#347: a tuned custom crossfeed is deliberate configuration.
        /// The two knobs only do anything at the custom level, but the keep-alive
        /// rule is what lets a user park the level back at Off without losing the
        /// tuning, so each knob keeps the config alive on its own.</summary>
        [Fact]
        public void Configured_TrueForCustomCrossfeedKnobsAlone()
        {
            Assert.True(SettingsService.IsDeviceSlotConfigDataConfigured(
                new DeviceSlotConfigData { AudioCrossfeedCutHz = 450 }));
            Assert.True(SettingsService.IsDeviceSlotConfigDataConfigured(
                new DeviceSlotConfigData { AudioCrossfeedFeedDb = 9.0d }));
            // The defaults are the library's own, so they must NOT read as tuning.
            Assert.False(SettingsService.IsDeviceSlotConfigDataConfigured(
                new DeviceSlotConfigData { AudioCrossfeedCutHz = 700, AudioCrossfeedFeedDb = 4.5d }));
        }

        /// <summary>The knobs clamp to libbs2b's accepted window on the way in,
        /// and the custom level survives the level clamp. A level of 9 that
        /// clamped to 8 would silently reinterpret a saved custom setting as the
        /// bs2b default preset.</summary>
        [Fact]
        public void CustomCrossfeed_ClampsToTheLibraryRange()
        {
            var cfg = new DeviceSlotConfig();
            Assert.Equal(700, cfg.AudioCrossfeedCutHz);
            Assert.Equal(4.5d, cfg.AudioCrossfeedFeedDb);
            Assert.False(cfg.AudioCrossfeedIsCustom);

            cfg.AudioCrossfeedLevel = 9;
            Assert.Equal(9, cfg.AudioCrossfeedLevel);
            Assert.True(cfg.AudioCrossfeedIsCustom);

            cfg.AudioCrossfeedCutHz = 10;
            Assert.Equal(300, cfg.AudioCrossfeedCutHz);
            cfg.AudioCrossfeedCutHz = 96000;
            Assert.Equal(2000, cfg.AudioCrossfeedCutHz);

            cfg.AudioCrossfeedFeedDb = 0d;
            Assert.Equal(1.0d, cfg.AudioCrossfeedFeedDb);
            cfg.AudioCrossfeedFeedDb = 200d;
            Assert.Equal(15.0d, cfg.AudioCrossfeedFeedDb);

            cfg.AudioCrossfeedLevel = 99;
            Assert.Equal(9, cfg.AudioCrossfeedLevel);
        }
    }
}
