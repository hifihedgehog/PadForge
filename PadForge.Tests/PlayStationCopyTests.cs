using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    /// <summary>Covers the "is this PlayStation config configured?" check that decides
    /// which controller's Lighting / Adaptive Trigger / Audio config a Copy / Paste /
    /// Copy From uses as the fallback. The old check ignored audio, so an audio-only
    /// setup read as empty and was skipped.</summary>
    public class PlayStationCopyTests
    {
        [Fact]
        public void Configured_FalseForAllDefault()
        {
            Assert.False(SettingsService.IsPlayStationConfigDataConfigured(new PlayStationSlotConfigData()));
            Assert.False(SettingsService.IsPlayStationConfigDataConfigured(null));
        }

        [Fact]
        public void Configured_TrueForAudioOnly()
        {
            Assert.True(SettingsService.IsPlayStationConfigDataConfigured(
                new PlayStationSlotConfigData { AudioPassthroughEnabled = true }));
            Assert.True(SettingsService.IsPlayStationConfigDataConfigured(
                new PlayStationSlotConfigData { AudioLightbarEnabled = true }));
            Assert.True(SettingsService.IsPlayStationConfigDataConfigured(
                new PlayStationSlotConfigData { AudioMirrorSourceId = "some-output" }));
        }

        [Fact]
        public void Configured_TrueForLightingOrTriggers()
        {
            Assert.True(SettingsService.IsPlayStationConfigDataConfigured(
                new PlayStationSlotConfigData { LightbarMode = LightbarMode.Static }));
            Assert.True(SettingsService.IsPlayStationConfigDataConfigured(
                new PlayStationSlotConfigData { LeftTriggerMode = AdaptiveTriggerMode.Weapon }));
        }
    }
}
