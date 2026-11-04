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

        /// <summary>#494: neither the Control This Device's Lighting switch
        /// nor a raised Lighting Hold makes a config count in either check.
        /// The checks choose the config that stands in for a device the
        /// source lacked, and a stand-in never carries the switch or the
        /// hold, so a config that sets nothing else has nothing to give and
        /// must not be chosen ahead of one that has.</summary>
        [Theory]
        [InlineData(false, PeripheralClaimLevel.Power)]
        [InlineData(false, PeripheralClaimLevel.HostMode)]
        [InlineData(true, PeripheralClaimLevel.Colors)]
        [InlineData(true, PeripheralClaimLevel.HostMode)]
        public void Configured_FalseForTheLightingSwitchOrARaisedHoldAlone(bool lit, PeripheralClaimLevel hold)
        {
            Assert.False(SettingsService.IsDeviceSlotConfigDataConfigured(
                new DeviceSlotConfigData { PeripheralLightingEnabled = lit, PeripheralClaimLevel = hold }));
            // A current save spells its untouched modes as Player Number.
            var current = new DeviceSlotConfigData
            {
                LightingRev = 1, LightbarMode = LightbarMode.PlayerNumber, PlayerLedMode = PlayerLedMode.PlayerNumber,
                PeripheralLightingEnabled = lit, PeripheralClaimLevel = hold,
            };
            Assert.False(SettingsService.IsDeviceSlotConfigDataConfigured(current));
            current.LightbarMode = LightbarMode.Static;
            Assert.True(SettingsService.IsDeviceSlotConfigDataConfigured(current));

            var cfg = new DeviceSlotConfig { PeripheralLightingEnabled = lit, PeripheralClaimLevel = hold };
            Assert.False(SettingsService.IsDeviceConfigConfigured(cfg));
            cfg.LightbarMode = LightbarMode.Static;
            Assert.True(SettingsService.IsDeviceConfigConfigured(cfg));
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

    /// <summary>
    /// Copy From and Paste land each source device's settings on the same
    /// device, and a destination device the source slot did not have on a
    /// stand-in source device's (#494). The stand-in is another device, so
    /// its Control This Device's Lighting switch never travels: a mouse's
    /// switch would make the destination controller take over a keyboard
    /// assigned only for its keys, and a DualSense's would let go of a
    /// keyboard the user had lit. The same device on both slots carries its
    /// switch. The Lighting Hold under the switch follows the same rule: a
    /// hold chosen for one device never reaches another, which it would
    /// keep from dimming its lights or take out of its on-board memory
    /// mode, and the same device on both slots carries its own.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class DeviceSlotConfigStandInTests : IDisposable
    {
        private static readonly System.Guid Mouse = new("00000000-0000-0000-0000-00000000c0a1");
        private static readonly System.Guid Keyboard = new("00000000-0000-0000-0000-00000000c0b2");
        private static readonly System.Guid DualSense = new("00000000-0000-0000-0000-00000000c0c3");
        private static readonly System.Guid Edge = new("00000000-0000-0000-0000-00000000c0d4");
        private readonly PadForge.Common.Input.SettingsCollection _settings = PadForge.Common.Input.SettingsManager.UserSettings;
        private readonly PadForge.Common.Input.DeviceCollection _devices = PadForge.Common.Input.SettingsManager.UserDevices;
        private readonly bool[] _created = PadForge.Common.Input.SettingsManager.SlotCreated;
        private readonly bool[] _enabled = PadForge.Common.Input.SettingsManager.SlotEnabled;
        private readonly System.Action _afterRefresh = SettingsService.AfterMappingSetsRefreshed;

        public DeviceSlotConfigStandInTests()
        {
            PadForge.Common.Input.SettingsManager.UserSettings = new PadForge.Common.Input.SettingsCollection();
            PadForge.Common.Input.SettingsManager.UserDevices = new PadForge.Common.Input.DeviceCollection();
            PadForge.Common.Input.SettingsManager.SlotCreated = new bool[PadForge.Common.Input.InputManager.MaxPads];
            PadForge.Common.Input.SettingsManager.SlotEnabled = new bool[PadForge.Common.Input.InputManager.MaxPads];
        }

        public void Dispose()
        {
            PadForge.Common.Input.SettingsManager.UserSettings = _settings;
            PadForge.Common.Input.SettingsManager.UserDevices = _devices;
            PadForge.Common.Input.SettingsManager.SlotCreated = _created;
            PadForge.Common.Input.SettingsManager.SlotEnabled = _enabled;
            SettingsService.AfterMappingSetsRefreshed = _afterRefresh;
        }

        private static (MainViewModel Vm, SettingsService Settings) Arrange()
        {
            var vm = new MainViewModel();
            return (vm, new SettingsService(vm));
        }

        [Fact]
        public void CopyFrom_KeepsAnUnmatchedDevicesOwnSwitch()
        {
            var (vm, settings) = Arrange();
            var mouse = vm.Pads[0].GetOrCreateDeviceConfig(Mouse);
            mouse.PeripheralLightingEnabled = true;
            mouse.LightbarMode = LightbarMode.Static;
            var keyboard = vm.Pads[1].GetOrCreateDeviceConfig(Keyboard);

            settings.CopySlotConfigsAcrossSlots(0, 1);

            Assert.Equal(LightbarMode.Static, keyboard.LightbarMode);
            Assert.False(keyboard.PeripheralLightingEnabled);
            Assert.True(mouse.PeripheralLightingEnabled);
        }

        /// <summary>How long a device's strips are is a fact about that device,
        /// so a copy carries it to the same device on another controller and
        /// leaves another device's own.</summary>
        [Fact]
        public void CopyFrom_CarriesStripCountsToTheSameDevice_AndLeavesAnotherDevicesOwn()
        {
            var (vm, settings) = Arrange();
            var source = vm.Pads[0].GetOrCreateDeviceConfig(Mouse);
            source.LedStripCounts = "30,0,12";
            source.LightbarMode = LightbarMode.Static;
            var sameDevice = vm.Pads[1].GetOrCreateDeviceConfig(Mouse);
            var keyboard = vm.Pads[1].GetOrCreateDeviceConfig(Keyboard);
            keyboard.LedStripCounts = "9";

            settings.CopySlotConfigsAcrossSlots(0, 1);

            Assert.Equal("30,0,12", sameDevice.LedStripCounts);
            Assert.Equal(LightbarMode.Static, keyboard.LightbarMode);
            Assert.Equal("9", keyboard.LedStripCounts);
        }

        /// <summary>A paste keeps the same rule, and leaves the clipboard's
        /// stand-in as it came, since one paste applies it to several devices.</summary>
        [Fact]
        public void Paste_KeepsAnUnmatchedDevicesStripCounts_AndCarriesTheSameDevicesOwn()
        {
            var (vm, settings) = Arrange();
            var keyboard = vm.Pads[1].GetOrCreateDeviceConfig(Keyboard);
            keyboard.LedStripCounts = "9";
            var mouse = vm.Pads[1].GetOrCreateDeviceConfig(Mouse);
            var standIn = new DeviceSlotConfigData
            {
                DeviceGuid = DualSense, LightingRev = 1, LightbarMode = LightbarMode.Static, LedStripCounts = "4",
            };
            var own = new DeviceSlotConfigData
            {
                DeviceGuid = Mouse, LightingRev = 1, LightbarMode = LightbarMode.Static, LedStripCounts = "7",
            };

            settings.ApplyDeviceSlotConfigsToSlot(1, new[] { standIn, own });

            Assert.Equal("9", keyboard.LedStripCounts);
            Assert.Equal("7", mouse.LedStripCounts);
            Assert.Equal("4", standIn.LedStripCounts);
        }

        [Fact]
        public void CopyFrom_NeverTurnsOffAnUnmatchedDevicesSwitch()
        {
            var (vm, settings) = Arrange();
            var pad = vm.Pads[0].GetOrCreateDeviceConfig(DualSense);
            pad.LightbarMode = LightbarMode.Static;
            var keyboard = vm.Pads[1].GetOrCreateDeviceConfig(Keyboard);
            keyboard.PeripheralLightingEnabled = true;

            settings.CopySlotConfigsAcrossSlots(0, 1);

            Assert.Equal(LightbarMode.Static, keyboard.LightbarMode);
            Assert.True(keyboard.PeripheralLightingEnabled);
        }

        [Fact]
        public void CopyFrom_CarriesTheSwitchForTheSameDevice()
        {
            var (vm, settings) = Arrange();
            vm.Pads[0].GetOrCreateDeviceConfig(Mouse).PeripheralLightingEnabled = true;
            var mouse = vm.Pads[1].GetOrCreateDeviceConfig(Mouse);

            settings.CopySlotConfigsAcrossSlots(0, 1);

            Assert.True(mouse.PeripheralLightingEnabled);
        }

        [Fact]
        public void Paste_KeepsAnUnmatchedDevicesOwnSwitch_AndCarriesTheSameDevicesOwn()
        {
            var (vm, settings) = Arrange();
            var keyboard = vm.Pads[1].GetOrCreateDeviceConfig(Keyboard);
            var mouse = vm.Pads[1].GetOrCreateDeviceConfig(Mouse);
            var standIn = new DeviceSlotConfigData
            {
                DeviceGuid = DualSense, LightingRev = 1, LightbarMode = LightbarMode.Static, PeripheralLightingEnabled = true,
            };
            var own = new DeviceSlotConfigData
            {
                DeviceGuid = Mouse, LightingRev = 1, LightbarMode = LightbarMode.Static, PeripheralLightingEnabled = true,
            };

            settings.ApplyDeviceSlotConfigsToSlot(1, new[] { standIn, own });

            Assert.Equal(LightbarMode.Static, keyboard.LightbarMode);
            Assert.False(keyboard.PeripheralLightingEnabled);
            Assert.True(mouse.PeripheralLightingEnabled);
            // The clipboard's own entry is left as it came.
            Assert.True(standIn.PeripheralLightingEnabled);

            keyboard.PeripheralLightingEnabled = true;
            standIn.PeripheralLightingEnabled = false;
            settings.ApplyDeviceSlotConfigsToSlot(1, new[] { standIn });
            Assert.True(keyboard.PeripheralLightingEnabled);
        }

        [Fact]
        public void CopyFrom_KeepsAnUnmatchedDevicesOwnHold()
        {
            var (vm, settings) = Arrange();
            var mouse = vm.Pads[0].GetOrCreateDeviceConfig(Mouse);
            mouse.PeripheralClaimLevel = PeripheralClaimLevel.HostMode;
            mouse.LightbarMode = LightbarMode.Static;
            var keyboard = vm.Pads[1].GetOrCreateDeviceConfig(Keyboard);

            settings.CopySlotConfigsAcrossSlots(0, 1);

            // The mouse's settings stood in for the keyboard's, all but its hold.
            Assert.Equal(LightbarMode.Static, keyboard.LightbarMode);
            Assert.Equal(PeripheralClaimLevel.Colors, keyboard.PeripheralClaimLevel);
            Assert.Equal(PeripheralClaimLevel.HostMode, mouse.PeripheralClaimLevel);
        }

        [Fact]
        public void CopyFrom_NeverLowersAnUnmatchedDevicesHold()
        {
            var (vm, settings) = Arrange();
            var pad = vm.Pads[0].GetOrCreateDeviceConfig(DualSense);
            pad.LightbarMode = LightbarMode.Static;
            var keyboard = vm.Pads[1].GetOrCreateDeviceConfig(Keyboard);
            keyboard.PeripheralClaimLevel = PeripheralClaimLevel.Power;

            settings.CopySlotConfigsAcrossSlots(0, 1);

            Assert.Equal(LightbarMode.Static, keyboard.LightbarMode);
            Assert.Equal(PeripheralClaimLevel.Power, keyboard.PeripheralClaimLevel);
        }

        [Fact]
        public void CopyFrom_CarriesTheHoldForTheSameDevice()
        {
            var (vm, settings) = Arrange();
            vm.Pads[0].GetOrCreateDeviceConfig(Mouse).PeripheralClaimLevel = PeripheralClaimLevel.Power;
            var mouse = vm.Pads[1].GetOrCreateDeviceConfig(Mouse);
            mouse.PeripheralClaimLevel = PeripheralClaimLevel.HostMode;

            settings.CopySlotConfigsAcrossSlots(0, 1);

            Assert.Equal(PeripheralClaimLevel.Power, mouse.PeripheralClaimLevel);
        }

        [Fact]
        public void Paste_KeepsAnUnmatchedDevicesOwnHold_AndCarriesTheSameDevicesOwn()
        {
            var (vm, settings) = Arrange();
            var keyboard = vm.Pads[1].GetOrCreateDeviceConfig(Keyboard);
            var mouse = vm.Pads[1].GetOrCreateDeviceConfig(Mouse);
            var standIn = new DeviceSlotConfigData
            {
                DeviceGuid = DualSense, LightingRev = 1, LightbarMode = LightbarMode.Static,
                PeripheralClaimLevel = PeripheralClaimLevel.HostMode,
            };
            var own = new DeviceSlotConfigData
            {
                DeviceGuid = Mouse, LightingRev = 1, LightbarMode = LightbarMode.Static,
                PeripheralClaimLevel = PeripheralClaimLevel.Power,
            };

            settings.ApplyDeviceSlotConfigsToSlot(1, new[] { standIn, own });

            Assert.Equal(LightbarMode.Static, keyboard.LightbarMode);
            Assert.Equal(PeripheralClaimLevel.Colors, keyboard.PeripheralClaimLevel);
            Assert.Equal(PeripheralClaimLevel.Power, mouse.PeripheralClaimLevel);
            // The clipboard's own entry is left as it came.
            Assert.Equal(PeripheralClaimLevel.HostMode, standIn.PeripheralClaimLevel);

            keyboard.PeripheralClaimLevel = PeripheralClaimLevel.Power;
            standIn.PeripheralClaimLevel = PeripheralClaimLevel.Colors;
            settings.ApplyDeviceSlotConfigsToSlot(1, new[] { standIn });
            Assert.Equal(PeripheralClaimLevel.Power, keyboard.PeripheralClaimLevel);
            Assert.Equal(PeripheralClaimLevel.Colors, standIn.PeripheralClaimLevel);
        }

        private static DeviceSlotConfig Select(PadViewModel pad, System.Guid device)
        {
            var mapped = new PadViewModel.MappedDeviceInfo { InstanceGuid = device, Name = "Device", IsOnline = true };
            pad.MappedDevices.Add(mapped);
            pad.SelectedMappedDevice = mapped;
            Assert.Same(pad.DeviceConfig, pad.GetOrCreateDeviceConfig(device));
            return pad.DeviceConfig;
        }

        /// <summary>A config that only its lighting switch or its hold sets
        /// is not the stand-in. The source slot's selected mouse holds a
        /// raised hold, or its switch on, and default modes, and the slot's
        /// DualSense Edge is set to a static color. A DualSense the source
        /// lacked takes the Edge's color, as it did before a mouse could
        /// hold either setting.</summary>
        [Theory]
        [InlineData(false, PeripheralClaimLevel.Power)]
        [InlineData(true, PeripheralClaimLevel.Colors)]
        public void CopyFrom_NeverChoosesAStandIn_ForItsSwitchOrItsHoldAlone(bool lit, PeripheralClaimLevel hold)
        {
            var (vm, settings) = Arrange();
            var mouse = Select(vm.Pads[0], Mouse);
            mouse.PeripheralLightingEnabled = lit;
            mouse.PeripheralClaimLevel = hold;
            var edge = vm.Pads[0].GetOrCreateDeviceConfig(Edge);
            edge.LightbarMode = LightbarMode.Static;
            var dualSense = vm.Pads[1].GetOrCreateDeviceConfig(DualSense);

            settings.CopySlotConfigsAcrossSlots(0, 1);

            Assert.Equal(LightbarMode.Static, dualSense.LightbarMode);
        }

        /// <summary>The same choice on a paste: the clipboard's slot-level
        /// entry sets its hold alone, so the entry that sets a color stands
        /// in for a device the clipboard has none for.</summary>
        [Fact]
        public void Paste_NeverChoosesAStandIn_ForItsSwitchOrItsHoldAlone()
        {
            var (vm, settings) = Arrange();
            var dualSense = vm.Pads[1].GetOrCreateDeviceConfig(DualSense);
            var slotLevel = new DeviceSlotConfigData
            {
                LightingRev = 1, LightbarMode = LightbarMode.PlayerNumber, PlayerLedMode = PlayerLedMode.PlayerNumber,
                PeripheralLightingEnabled = true, PeripheralClaimLevel = PeripheralClaimLevel.HostMode,
            };
            var edge = new DeviceSlotConfigData
            {
                DeviceGuid = Edge, LightingRev = 1, LightbarMode = LightbarMode.Static, PlayerLedMode = PlayerLedMode.PlayerNumber,
            };

            settings.ApplyDeviceSlotConfigsToSlot(1, new[] { slotLevel, edge });

            Assert.Equal(LightbarMode.Static, dualSense.LightbarMode);
        }

        /// <summary>A profile's slot-level entry mirrors the device that was
        /// selected when the profile was saved. Applied while another
        /// device is selected, it never puts that device's switch or hold
        /// on the selected one, not even for the moment before the selected
        /// device's own entry lands: each write runs a dispatcher pass, and
        /// a lighting worker can wake behind it. A selected device the
        /// profile has no entry for takes the slot-level entry as a
        /// stand-in, with its own switch and hold kept.</summary>
        [Fact]
        public void AProfilesSlotLevelEntry_NeverPutsAnotherDevicesSwitchOrHold_OnTheSelectedDevice()
        {
            var (vm, settings) = Arrange();
            PadForge.Common.Input.SettingsManager.SlotCreated[1] = true;
            var pad = vm.Pads[1];
            var keyboard = Select(pad, Keyboard);
            var seen = new List<(bool Lit, PeripheralClaimLevel Hold)>();
            keyboard.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(DeviceSlotConfig.PeripheralLightingEnabled)
                    or nameof(DeviceSlotConfig.PeripheralClaimLevel))
                    seen.Add((keyboard.PeripheralLightingEnabled, keyboard.PeripheralClaimLevel));
            };
            DeviceSlotConfigData Entry(System.Guid device, LightbarMode mode, bool lit, PeripheralClaimLevel hold) => new()
            {
                SlotIndex = 1, DeviceGuid = device, LightingRev = 1, LightbarMode = mode, PlayerLedMode = PlayerLedMode.PlayerNumber,
                PeripheralLightingEnabled = lit, PeripheralClaimLevel = hold,
            };
            // Saved with the mouse selected: the slot-level entry is the mouse's.
            var slotLevel = Entry(System.Guid.Empty, LightbarMode.Static, lit: true, PeripheralClaimLevel.HostMode);
            var mouse = Entry(Mouse, LightbarMode.Static, lit: true, PeripheralClaimLevel.HostMode);
            var own = Entry(Keyboard, LightbarMode.Rainbow, lit: false, PeripheralClaimLevel.Colors);

            settings.ApplyDeviceSlotConfigs(new[] { slotLevel, mouse, own });

            Assert.Empty(seen);
            Assert.False(keyboard.PeripheralLightingEnabled);
            Assert.Equal(PeripheralClaimLevel.Colors, keyboard.PeripheralClaimLevel);
            Assert.Equal(LightbarMode.Rainbow, keyboard.LightbarMode);
            // The mouse's own entry carries the mouse's own pair.
            Assert.True(pad.GetOrCreateDeviceConfig(Mouse).PeripheralLightingEnabled);
            Assert.Equal(PeripheralClaimLevel.HostMode, pad.GetOrCreateDeviceConfig(Mouse).PeripheralClaimLevel);

            // The other order of the set changes nothing.
            settings.ApplyDeviceSlotConfigs(new[] { own, mouse, slotLevel });
            Assert.Empty(seen);
            Assert.Equal(LightbarMode.Rainbow, keyboard.LightbarMode);

            // No entry for the keyboard: the slot-level one stands in, and
            // the keyboard's own pair stays.
            keyboard.PeripheralClaimLevel = PeripheralClaimLevel.Power;
            seen.Clear();
            settings.ApplyDeviceSlotConfigs(new[] { slotLevel, mouse });
            Assert.Empty(seen);
            Assert.Equal(LightbarMode.Static, keyboard.LightbarMode);
            Assert.False(keyboard.PeripheralLightingEnabled);
            Assert.Equal(PeripheralClaimLevel.Power, keyboard.PeripheralClaimLevel);
        }

        /// <summary>A set with no entry for any device of a slot is a save
        /// from before lighting was kept per device, or one made while the
        /// slot held none. Its slot-level entry reaches every device of the
        /// slot as a stand-in: no device's switch is turned on or off, and
        /// no hold changed, by an entry that was never its own. The config
        /// bound while no device is selected is nobody's, and takes the
        /// entry whole.</summary>
        [Fact]
        public void ASlotLevelEntryWithNoDeviceEntries_ReachesEachDeviceAsAStandIn()
        {
            var (vm, settings) = Arrange();
            PadForge.Common.Input.SettingsManager.SlotCreated[1] = true;
            var pad = vm.Pads[1];
            var keyboard = pad.GetOrCreateDeviceConfig(Keyboard);
            var mouse = pad.GetOrCreateDeviceConfig(Mouse);
            mouse.PeripheralLightingEnabled = true;
            mouse.PeripheralClaimLevel = PeripheralClaimLevel.Power;
            var unbound = pad.DeviceConfig;
            var slotLevel = new DeviceSlotConfigData
            {
                SlotIndex = 1, LightingRev = 1, LightbarMode = LightbarMode.Static, PlayerLedMode = PlayerLedMode.PlayerNumber,
                PeripheralLightingEnabled = true, PeripheralClaimLevel = PeripheralClaimLevel.HostMode,
            };

            settings.ApplyDeviceSlotConfigs(new[] { slotLevel });

            Assert.Equal(LightbarMode.Static, keyboard.LightbarMode);
            Assert.False(keyboard.PeripheralLightingEnabled);
            Assert.Equal(PeripheralClaimLevel.Colors, keyboard.PeripheralClaimLevel);
            Assert.Equal(LightbarMode.Static, mouse.LightbarMode);
            Assert.True(mouse.PeripheralLightingEnabled);
            Assert.Equal(PeripheralClaimLevel.Power, mouse.PeripheralClaimLevel);
            Assert.Same(unbound, pad.DeviceConfig);
            Assert.True(unbound.PeripheralLightingEnabled);
            Assert.Equal(PeripheralClaimLevel.HostMode, unbound.PeripheralClaimLevel);
            // The entry itself is left as it came.
            Assert.True(slotLevel.PeripheralLightingEnabled);
            Assert.Equal(PeripheralClaimLevel.HostMode, slotLevel.PeripheralClaimLevel);
        }

        /// <summary>The switch and the hold are kept together: a stand-in
        /// that would turn a keyboard's lighting on and raise its hold
        /// changes neither, and the keyboard's own pair survives a stand-in
        /// that holds neither.</summary>
        [Fact]
        public void AStandIn_LeavesTheSwitchAndTheHoldOfADeviceTogether()
        {
            var (vm, settings) = Arrange();
            var mouse = vm.Pads[0].GetOrCreateDeviceConfig(Mouse);
            mouse.PeripheralLightingEnabled = true;
            mouse.PeripheralClaimLevel = PeripheralClaimLevel.HostMode;
            var keyboard = vm.Pads[1].GetOrCreateDeviceConfig(Keyboard);

            settings.CopySlotConfigsAcrossSlots(0, 1);
            Assert.False(keyboard.PeripheralLightingEnabled);
            Assert.Equal(PeripheralClaimLevel.Colors, keyboard.PeripheralClaimLevel);

            keyboard.PeripheralLightingEnabled = true;
            keyboard.PeripheralClaimLevel = PeripheralClaimLevel.Power;
            mouse.PeripheralLightingEnabled = false;
            mouse.PeripheralClaimLevel = PeripheralClaimLevel.Colors;
            mouse.LightbarMode = LightbarMode.Static;
            settings.CopySlotConfigsAcrossSlots(0, 1);
            Assert.Equal(LightbarMode.Static, keyboard.LightbarMode);
            Assert.True(keyboard.PeripheralLightingEnabled);
            Assert.Equal(PeripheralClaimLevel.Power, keyboard.PeripheralClaimLevel);
        }
    }
}
