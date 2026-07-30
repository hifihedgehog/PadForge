using System;
using System.Linq;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Touchpad;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Touchpad gesture / gating settings collapse from per-(device, pad)
    /// to per-DEVICE: enabling a setting applies to every touchpad the
    /// device enumerates (a Steam Controller has 2 pads). The per-pad
    /// distinction survives only in the output descriptor strings the user
    /// picks in the mapping grid ("Touchpad 0 StickX" vs "Touchpad 1
    /// StickX"). These pin the shared resolver, its configured-beats-default
    /// coalesce, and the end-to-end picker contract that both pads light up.
    /// </summary>
    public class TouchpadPerDeviceSettingsTests
    {
        private const string G = "11111111-2222-3333-4444-555555555555";

        private static TouchpadSettingsEntry Entry(string guid, int idx, TouchpadGestureSettings s)
            => new TouchpadSettingsEntry { DeviceGuid = guid, TouchpadIndex = idx, Settings = s };

        // ── 1: one per-device entry lights up every pad ──

        [Fact]
        public void ResolveForDevice_ReturnsEnabledForEveryPad()
        {
            var entries = new[]
            {
                Entry(G, 0, new TouchpadGestureSettings { Enabled = true, EnableJoystickOutput = true }),
            };

            // Pre-change, the per-pad matcher returned Default() (disabled)
            // for any pad index other than the stored one. Now a single
            // per-device entry resolves the same enabled bundle no matter
            // which pad the picker asks about.
            var resolved = TouchpadGestureSettings.ResolveForDevice(entries, G);
            Assert.True(resolved.Enabled);
            Assert.True(resolved.EnableJoystickOutput);
            Assert.Same(entries[0].Settings, resolved);

            // The resolver has no pad key: repeated calls yield the same
            // stored bundle (what pad 0 and pad 1 both see).
            var again = TouchpadGestureSettings.ResolveForDevice(entries, G);
            Assert.Same(resolved, again);

            // A different device guid never matches this entry.
            var miss = TouchpadGestureSettings.ResolveForDevice(entries, "99999999-0000-0000-0000-000000000000");
            Assert.False(miss.Enabled);
            Assert.False(miss.EnableJoystickOutput);
        }

        // ── 2: legacy per-pad array coalesces (configured wins) ──

        [Fact]
        public void ResolveForDevice_CoalescesLegacyPerPadEntries()
        {
            var entries = new[]
            {
                Entry(G, 0, TouchpadGestureSettings.Default()),                                          // pad 0: untouched
                Entry(G, 1, new TouchpadGestureSettings { Enabled = true, EnableJoystickOutput = true }), // pad 1: configured
            };

            var resolved = TouchpadGestureSettings.ResolveForDevice(entries, G);
            Assert.True(resolved.Enabled);
            Assert.True(resolved.EnableJoystickOutput);
            Assert.Same(entries[1].Settings, resolved);

            // Order-independent: the configured entry still wins when it is
            // first in the array.
            var swapped = new[] { entries[1], entries[0] };
            Assert.Same(entries[1].Settings, TouchpadGestureSettings.ResolveForDevice(swapped, G));
        }

        // ── 3: mouse-only tuning counts as configured (CORRECTION 2) ──

        [Fact]
        public void ResolveForDevice_PrefersMouseConfiguredOverDefault()
        {
            // Pad 1's masters are all OFF; it differs from Default only in
            // mouse sensitivity. IsConfigured must still flag it so it beats
            // the pristine pad-0 entry (a pad set up mouse-only must win).
            var entries = new[]
            {
                Entry(G, 0, TouchpadGestureSettings.Default()),
                Entry(G, 1, new TouchpadGestureSettings { MouseSensitivityX = 2.0f }),
            };

            var resolved = TouchpadGestureSettings.ResolveForDevice(entries, G);
            Assert.Same(entries[1].Settings, resolved);
            Assert.Equal(2.0f, resolved.MouseSensitivityX);
            Assert.False(resolved.Enabled); // masters stay off; it still won

            // Every off-default lane counts as configured; a pristine Default does not.
            Assert.True(TouchpadGestureSettings.IsConfigured(new TouchpadGestureSettings { MouseInvertY = true }));
            Assert.True(TouchpadGestureSettings.IsConfigured(new TouchpadGestureSettings { PointerStretchX = 1.5f }));
            Assert.True(TouchpadGestureSettings.IsConfigured(new TouchpadGestureSettings { SwipeHapticsIntensity = 0.8f }));
            Assert.True(TouchpadGestureSettings.IsConfigured(new TouchpadGestureSettings { SwipeDistanceThreshold = 0.30f }));
            Assert.False(TouchpadGestureSettings.IsConfigured(TouchpadGestureSettings.Default()));
        }

        // ── 5: end-to-end, both pads surface StickX (CORRECTION 4) ──

        [Fact]
        public void BuildInputChoices_LightsUpEveryPad_ThroughTheResolver()
        {
            // A 2-pad device with no live wrapper: numPads comes from the
            // persisted CapTouchpadCount (MappingDisplayResolver.cs:1453).
            var ud = new UserDevice
            {
                InstanceGuid = Guid.Parse(G),
                ProductName = "Steam Controller",
                CapType = InputDeviceType.Gamepad,
                HasTouchpad = true,
                CapTouchpadCount = 2,
            };

            // The production picker delegate resolves per-device and ignores
            // the pad index (InputService seam A). One stored entry therefore
            // enables joystick output on BOTH pads.
            var entries = new[]
            {
                Entry(G, 0, new TouchpadGestureSettings { Enabled = true, EnableJoystickOutput = true }),
            };
            Func<int, TouchpadGestureSettings> settingsForPad =
                _ => TouchpadGestureSettings.ResolveForDevice(entries, G);

            var choices = MappingDisplayResolver.BuildInputChoices(ud, settingsForPad, null)
                          ?? System.Array.Empty<InputChoice>();
            var descriptors = choices.Select(c => c.Descriptor).ToArray();

            Assert.Contains("Touchpad 0 StickX", descriptors);
            Assert.Contains("Touchpad 1 StickX", descriptors);
        }
    }

    /// <summary>
    /// Write-side canonicalization (CORRECTION 3 + prune): the Touchpad tab
    /// pushes settings per DEVICE, so however many legacy per-pad entries a
    /// device carried, a save leaves exactly ONE entry for it stamped index
    /// 0. Runs serialized because it swaps SettingsManager statics.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class TouchpadPerDeviceSyncTests : IDisposable
    {
        private static readonly Guid Dev = new("dddddddd-eeee-ffff-0000-111111111111");

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;

        public TouchpadPerDeviceSyncTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
        }

        private static PadSetting Arrange(TouchpadSettingsEntry[] legacy)
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();

            var ud = new UserDevice
            {
                InstanceGuid = Dev,
                ProductName = "Steam Controller",
                CapType = InputDeviceType.Gamepad,
                HasTouchpad = true,
                CapTouchpadCount = 2,
                IsOnline = true,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);

            var ps = new PadSetting { TouchpadSettings = legacy };
            var us = new UserSetting { InstanceGuid = Dev, MapTo = 0 };
            us.SetPadSetting(ps);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(us);
            return us.GetPadSetting();
        }

        [Fact]
        public void Push_StampsRegionSchema_SoADeliberateFullScreenResetSurvives()
        {
            // The load-bearing half of moving the authored-but-default repair
            // into the resolver. Anything the push writes is current user
            // intent, so it must be stamped post-repair. Without the stamp a
            // freshly authored entry carries schema 0, the resolver repairs
            // it, and a deliberate reset to full screen (authored true at the
            // identity region) is cleared, which is the precise bug
            // PointerRegionAuthored exists to prevent.
            var ps = Arrange(null);
            var vm = new PadViewModel(0);
            vm.LoadTouchpadGestureSettingsForActiveDevice();

            // Drag the region away and back, which is how a user
            // deliberately lands on full screen. Assigning 1.0 straight
            // onto the 1.0 default is a no-op: SetProperty returns false,
            // nothing pushes, and the test would prove nothing.
            vm.TouchpadPointerRegionSizeX = 0.6;
            vm.TouchpadPointerRegionSizeX = 1.0;

            Assert.NotNull(ps.TouchpadSettings);   // the push actually ran

            var entry = ps.TouchpadSettings.Single(e => e.TouchpadIndex == 0);
            Assert.True(entry.Settings.PointerRegionAuthored);
            Assert.Equal(1, entry.Settings.RegionSchema);

            // And it must survive the resolver, which is where the repair runs.
            var resolved = TouchpadGestureSettings.ResolveEntryForPad(
                ps.TouchpadSettings, Dev.ToString(), 0);
            Assert.True(resolved.Settings.PointerRegionAuthored);
        }

        [Fact]
        public void Sync_WritesOneEntryPerPad_AndTheSelectorPivotsTheWholeTab()
        {
            // CONTRACT CHANGE. Settings used to collapse to one entry per
            // device, with the pad selector driving only the recorder and
            // preview. They are now per (device, pad): a controller's left and
            // right pads are different hardware in different places under
            // different thumbs, and the owner asked for the whole tab to
            // follow the selector rather than the screen region alone.
            string g = Dev.ToString();
            var ps = Arrange(new[]
            {
                new TouchpadSettingsEntry { DeviceGuid = g, TouchpadIndex = 1,
                    Settings = new TouchpadGestureSettings { Enabled = true } },
                new TouchpadSettingsEntry { DeviceGuid = g, TouchpadIndex = 0,
                    Settings = TouchpadGestureSettings.Default() },
            });

            var vm = new PadViewModel(0);
            vm.LoadTouchpadGestureSettingsForActiveDevice();

            // Pad 0's own entry, not the "configured winner" across the device.
            Assert.False(vm.TouchpadGesturesEnabled);
            vm.TouchpadEnableJoystickOutput = true;

            var pad0 = ps.TouchpadSettings.Single(e => e.TouchpadIndex == 0);
            var pad1 = ps.TouchpadSettings.Single(e => e.TouchpadIndex == 1);
            Assert.True(pad0.Settings.EnableJoystickOutput);
            // Pad 1 is untouched: the edit belonged to pad 0 alone.
            Assert.False(pad1.Settings.EnableJoystickOutput);
            Assert.True(pad1.Settings.Enabled);

            // Switching pads pulls that pad's whole bundle in.
            vm.SelectedTouchpadIndex = 1;
            Assert.True(vm.TouchpadGesturesEnabled);          // pad 1 had Enabled
            Assert.False(vm.TouchpadEnableJoystickOutput);    // pad 0's edit did not leak

            vm.TouchpadEnableTaps = true;
            Assert.True(ps.TouchpadSettings.Single(e => e.TouchpadIndex == 1).Settings.EnableTaps);
            Assert.False(ps.TouchpadSettings.Single(e => e.TouchpadIndex == 0).Settings.EnableTaps);
        }

        [Fact]
        public void PositiveControl_BothPadEntriesSurviveAPush()
        {
            // Without this the test above could pass on a push that deleted
            // the sibling instead of leaving it alone.
            string g = Dev.ToString();
            var ps = Arrange(new[]
            {
                new TouchpadSettingsEntry { DeviceGuid = g, TouchpadIndex = 0,
                    Settings = TouchpadGestureSettings.Default() },
                new TouchpadSettingsEntry { DeviceGuid = g, TouchpadIndex = 1,
                    Settings = TouchpadGestureSettings.Default() },
            });
            var vm = new PadViewModel(0);
            vm.LoadTouchpadGestureSettingsForActiveDevice();
            vm.TouchpadEnableTaps = true;
            Assert.Equal(2, ps.TouchpadSettings.Count(e => e.TouchpadIndex is 0 or 1));
        }
    }
}
