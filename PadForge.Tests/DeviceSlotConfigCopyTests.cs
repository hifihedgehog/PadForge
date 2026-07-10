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
    }
}
