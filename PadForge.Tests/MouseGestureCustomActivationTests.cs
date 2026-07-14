using System;
using System.IO;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.Mouse;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Pins the mouse-gesture Custom activation contracts (discussion #216):
    /// the recorded cross-device input arms session index 5 exactly like a
    /// mouse button arms its own session (held = active, buttons while
    /// pressed, axes past the button threshold), the mask composer's
    /// guards (empty descriptor stays inert, unselected bit costs nothing,
    /// a sixth PHYSICAL mouse button can never bleed into the Custom
    /// session), the descriptor grammar's index-5 family, and the
    /// persistence legs (Clone, checksum dedup trap, XML round-trip,
    /// CopyFrom deep copy).
    /// </summary>
    public class MouseGestureCustomActivationTests : IDisposable
    {
        private const int CustomBit = 1 << MouseGestureContext.CustomButtonIndex;
        private const string DeviceA = "aaaaaaaa-1111-2222-3333-444444444444";
        private const string DeviceB = "bbbbbbbb-1111-2222-3333-444444444444";

        private readonly Func<string, string, int, bool> _savedProvider;

        public MouseGestureCustomActivationTests()
        {
            _savedProvider = SourceCoercion.ButtonHeldProvider;
        }

        public void Dispose()
        {
            SourceCoercion.ButtonHeldProvider = _savedProvider;
        }

        private static MouseGestureSettings CustomEnabled(
            string descriptor = "Button 5", string deviceGuid = DeviceA,
            int extraButtons = 0)
            => new MouseGestureSettings
            {
                Enabled = true,
                GestureButtons = CustomBit | extraButtons,
                CustomEngageButton = descriptor,
                CustomEngageDeviceGuid = deviceGuid,
                FlickThresholdCounts = 150,
                CooldownMs = 100,
            };

        [Fact]
        public void Custom_Held_Arms_Session_And_Classifies_At_Release()
        {
            bool held = false;
            SourceCoercion.ButtonHeldProvider = (guid, desc, slot) => held;

            var ctx = new MouseGestureContext();
            var s = CustomEnabled();

            // Hold the custom input (e.g. RB on a gamepad), flick left,
            // release: index 5's Left fires, exactly one key.
            held = true;
            MouseGestureRecognizer.Update(ctx, s,
                MouseGestureRecognizer.ComposePressedMask(0, s, 0), 0, 0, 1000);
            MouseGestureRecognizer.Update(ctx, s,
                MouseGestureRecognizer.ComposePressedMask(0, s, 0), -500, 20, 1010);
            held = false;
            MouseGestureRecognizer.Update(ctx, s,
                MouseGestureRecognizer.ComposePressedMask(0, s, 0), 0, 0, 1020);

            Assert.Contains("5 Left", ctx.FiredGesturesThisFrame);
            Assert.Single(ctx.FiredGesturesThisFrame);
        }

        [Fact]
        public void Custom_Below_Flick_Threshold_Fires_Click()
        {
            bool held = true;
            SourceCoercion.ButtonHeldProvider = (guid, desc, slot) => held;

            var ctx = new MouseGestureContext();
            var s = CustomEnabled();
            MouseGestureRecognizer.Update(ctx, s,
                MouseGestureRecognizer.ComposePressedMask(0, s, 0), 0, 0, 1000);
            MouseGestureRecognizer.Update(ctx, s,
                MouseGestureRecognizer.ComposePressedMask(0, s, 0), 40, -30, 1010);
            held = false;
            MouseGestureRecognizer.Update(ctx, s,
                MouseGestureRecognizer.ComposePressedMask(0, s, 0), 0, 0, 1020);

            Assert.Contains("5 Click", ctx.FiredGesturesThisFrame);
            Assert.Single(ctx.FiredGesturesThisFrame);
        }

        [Fact]
        public void Custom_Session_Coexists_With_Mouse_Button_Sessions()
        {
            // X1 (bit 3) and Custom both selected: each runs its own
            // session, the #200 independent-session contract extended to
            // index 5.
            SourceCoercion.ButtonHeldProvider = (guid, desc, slot) => true;
            var ctx = new MouseGestureContext();
            var s = CustomEnabled(extraButtons: 1 << 3);

            // Both held, flick right, custom releases first.
            int both = MouseGestureRecognizer.ComposePressedMask(1 << 3, s, 0);
            Assert.Equal((1 << 3) | CustomBit, both);
            MouseGestureRecognizer.Update(ctx, s, both, 0, 0, 1000);
            MouseGestureRecognizer.Update(ctx, s, both, 300, 0, 1010);
            SourceCoercion.ButtonHeldProvider = (guid, desc, slot) => false;
            MouseGestureRecognizer.Update(ctx, s,
                MouseGestureRecognizer.ComposePressedMask(1 << 3, s, 0), 0, 0, 1020);
            Assert.Contains("5 Right", ctx.FiredGesturesThisFrame);
            Assert.DoesNotContain("3 Right", ctx.FiredGesturesThisFrame);

            // X1 keeps accumulating and classifies at ITS release.
            MouseGestureRecognizer.Update(ctx, s,
                MouseGestureRecognizer.ComposePressedMask(0, s, 0), 0, 0, 1030);
            Assert.Contains("3 Right", ctx.FiredGesturesThisFrame);
        }

        [Fact]
        public void Empty_Descriptor_Never_Arms_Even_When_Provider_Passes_Through()
        {
            // ButtonHeldProvider's engage-family convention returns TRUE
            // for an empty descriptor (unconfigured = pass-through). The
            // Custom gesture button must NOT inherit that: unconfigured
            // stays inert, so the composer may not consult the provider.
            int calls = 0;
            SourceCoercion.ButtonHeldProvider = (guid, desc, slot) => { calls++; return true; };

            var s = CustomEnabled(descriptor: "");
            int mask = MouseGestureRecognizer.ComposePressedMask(0, s, 0);
            Assert.Equal(0, mask);
            Assert.Equal(0, calls);
        }

        [Fact]
        public void Unselected_Custom_Bit_Never_Consults_Provider()
        {
            int calls = 0;
            SourceCoercion.ButtonHeldProvider = (guid, desc, slot) => { calls++; return true; };

            var s = CustomEnabled();
            s.GestureButtons = 1 << 3; // X1 only, Custom unselected.
            int mask = MouseGestureRecognizer.ComposePressedMask(0, s, 0);
            Assert.Equal(0, mask);
            Assert.Equal(0, calls);
        }

        [Fact]
        public void Disabled_Card_Never_Consults_Provider()
        {
            // Update ignores the mask while the card is disabled, so the
            // composer must not spend the cross-device read either.
            int calls = 0;
            SourceCoercion.ButtonHeldProvider = (guid, desc, slot) => { calls++; return true; };

            var s = CustomEnabled();
            s.Enabled = false;
            int mask = MouseGestureRecognizer.ComposePressedMask(0, s, 0);
            Assert.Equal(0, mask);
            Assert.Equal(0, calls);
        }

        [Fact]
        public void Sixth_Physical_Mouse_Button_Cannot_Bleed_Into_Custom()
        {
            // A mouse reporting 6+ buttons: raw bit 5 in the wrapper's
            // button state must be stripped, or the extra physical button
            // would arm the Custom session uninvited.
            SourceCoercion.ButtonHeldProvider = (guid, desc, slot) => false;
            var s = CustomEnabled();
            int mask = MouseGestureRecognizer.ComposePressedMask(1 << 5, s, 0);
            Assert.Equal(0, mask);

            // Same strip with Custom genuinely held: the held state comes
            // from the provider, not the raw bit.
            SourceCoercion.ButtonHeldProvider = (guid, desc, slot) => true;
            mask = MouseGestureRecognizer.ComposePressedMask(1 << 5, s, 0);
            Assert.Equal(CustomBit, mask);
        }

        [Fact]
        public void Composer_Passes_The_Settings_Device_Descriptor_And_Slot()
        {
            // Cross-device resolution: the provider receives exactly the
            // recorded (device GUID, descriptor) pair of THIS settings
            // instance plus the evaluating slot, so two (slot, device)
            // pairs with different recordings stay isolated.
            string seenGuid = null, seenDesc = null;
            int seenSlot = -1;
            SourceCoercion.ButtonHeldProvider = (guid, desc, slot) =>
            {
                seenGuid = guid; seenDesc = desc; seenSlot = slot;
                return true;
            };

            var a = CustomEnabled(descriptor: "Button 5", deviceGuid: DeviceA);
            var b = CustomEnabled(descriptor: "Axis 2", deviceGuid: DeviceB);

            Assert.Equal(CustomBit, MouseGestureRecognizer.ComposePressedMask(0, a, 3));
            Assert.Equal(DeviceA, seenGuid);
            Assert.Equal("Button 5", seenDesc);
            Assert.Equal(3, seenSlot);

            Assert.Equal(CustomBit, MouseGestureRecognizer.ComposePressedMask(0, b, 7));
            Assert.Equal(DeviceB, seenGuid);
            Assert.Equal("Axis 2", seenDesc);
            Assert.Equal(7, seenSlot);
        }

        [Fact]
        public void Axis_Past_Button_Threshold_Engages_Below_Does_Not()
        {
            // A racing-wheel accelerator pedal as the custom activation:
            // wire the provider the way InputService wires it (the same
            // EvaluateForButtonTarget read the Aim Engage settle uses,
            // threshold 50), and drive a full-axis value across the
            // threshold. 0..65535 scale, 50% = 32767.
            var state = new CustomInputState();
            var synth = new MappingSource { Kind = "Direct" };
            SourceCoercion.ButtonHeldProvider = (guid, desc, slot) =>
            {
                if (string.IsNullOrEmpty(desc)) return true;
                synth.DeviceGuid = guid;
                synth.Descriptor = desc;
                return SourceCoercion.EvaluateForButtonTarget(state, synth, 50, slot);
            };

            var s = CustomEnabled(descriptor: "Axis 2", deviceGuid: DeviceA);

            state.Axis[2] = (int)(0.40 * 65535); // pedal at 40%: below.
            Assert.Equal(0, MouseGestureRecognizer.ComposePressedMask(0, s, 0));

            state.Axis[2] = (int)(0.60 * 65535); // pedal at 60%: engaged.
            Assert.Equal(CustomBit, MouseGestureRecognizer.ComposePressedMask(0, s, 0));

            // And the armed session classifies like any other: press past
            // threshold, flick down, ease off the pedal to release.
            var ctx = new MouseGestureContext();
            MouseGestureRecognizer.Update(ctx, s,
                MouseGestureRecognizer.ComposePressedMask(0, s, 0), 0, 0, 1000);
            MouseGestureRecognizer.Update(ctx, s,
                MouseGestureRecognizer.ComposePressedMask(0, s, 0), -10, 500, 1010);
            state.Axis[2] = 0;
            MouseGestureRecognizer.Update(ctx, s,
                MouseGestureRecognizer.ComposePressedMask(0, s, 0), 0, 0, 1020);
            Assert.Contains("5 Down", ctx.FiredGesturesThisFrame);
        }

        [Fact]
        public void Descriptor_Grammar_Covers_Index_Five()
        {
            Assert.Equal(MouseGestureContext.ButtonCount,
                MouseGestureRecognizer.Keys.Length);
            Assert.Equal("5 Left", MouseGestureRecognizer.Keys[5][0]);
            Assert.Equal("5 Click", MouseGestureRecognizer.Keys[5][4]);
            Assert.Equal(SourceCoercion.SourceType.MouseGesture,
                SourceCoercion.ClassifyDescriptor("Mouse Gesture 5 Left"));
            // The parsed name IS the fired-set key, same as indices 0-4.
            Assert.Equal(MouseGestureRecognizer.Keys[5][0],
                SourceCoercion.ParseMouseGestureName("Mouse Gesture 5 Left"));
        }

        [Fact]
        public void Clone_Carries_Custom_Fields()
        {
            var s = CustomEnabled(descriptor: "Slider 0", deviceGuid: DeviceB);
            var c = s.Clone();
            Assert.Equal("Slider 0", c.CustomEngageButton);
            Assert.Equal(DeviceB, c.CustomEngageDeviceGuid);
            Assert.Equal(s.GestureButtons, c.GestureButtons);

            var d = MouseGestureSettings.Default();
            Assert.Equal("", d.CustomEngageButton);
            Assert.Equal("", d.CustomEngageDeviceGuid);
            Assert.Equal(0, d.GestureButtons & CustomBit);
        }

        [Fact]
        public void Checksum_Differs_When_Custom_Fields_Differ()
        {
            // The #200 dedup-by-checksum trap extended to the new fields:
            // two devices identical except for the recorded custom input
            // must hash differently, or SaveToFile drops one on relaunch.
            static PadSetting WithCustom(string desc, string guid)
                => new PadSetting
                {
                    MouseGestureSettings = new[]
                    {
                        new MouseGestureSettingsEntry
                        {
                            DeviceGuid = "11111111-2222-3333-4444-555555555555",
                            Settings = new MouseGestureSettings
                            {
                                Enabled = true,
                                GestureButtons = CustomBit,
                                CustomEngageButton = desc,
                                CustomEngageDeviceGuid = guid,
                            },
                        },
                    },
                };

            var a = WithCustom("Button 5", DeviceA);
            var b = WithCustom("Axis 2", DeviceA);
            var c = WithCustom("Button 5", DeviceB);
            a.UpdateChecksum();
            b.UpdateChecksum();
            c.UpdateChecksum();
            Assert.NotEqual(a.PadSettingChecksum, b.PadSettingChecksum);
            Assert.NotEqual(a.PadSettingChecksum, c.PadSettingChecksum);
        }

        [Fact]
        public void Xml_Round_Trip_Preserves_Custom_Fields()
        {
            // The settings file's actual serialization surface: the new
            // XmlAttributes must survive PadSetting save/load.
            var src = new PadSetting
            {
                MouseGestureSettings = new[]
                {
                    new MouseGestureSettingsEntry
                    {
                        DeviceGuid = DeviceA,
                        Settings = CustomEnabled(descriptor: "Axis 2", deviceGuid: DeviceB),
                    },
                },
            };

            var ser = new XmlSerializer(typeof(PadSetting));
            using var sw = new StringWriter();
            ser.Serialize(sw, src);
            using var sr = new StringReader(sw.ToString());
            var loaded = (PadSetting)ser.Deserialize(sr);

            Assert.NotNull(loaded.MouseGestureSettings);
            var s = Assert.Single(loaded.MouseGestureSettings).Settings;
            Assert.Equal(CustomBit, s.GestureButtons & CustomBit);
            Assert.Equal("Axis 2", s.CustomEngageButton);
            Assert.Equal(DeviceB, s.CustomEngageDeviceGuid);
        }

        [Fact]
        public void CopyFrom_DeepCopies_Custom_Fields()
        {
            var src = new PadSetting
            {
                MouseGestureSettings = new[]
                {
                    new MouseGestureSettingsEntry
                    {
                        DeviceGuid = DeviceA,
                        Settings = CustomEnabled(descriptor: "Button 5", deviceGuid: DeviceB),
                    },
                },
            };
            var dst = new PadSetting();
            dst.CopyFrom(src);
            Assert.Equal("Button 5", dst.MouseGestureSettings[0].Settings.CustomEngageButton);
            // Deep, not shared.
            dst.MouseGestureSettings[0].Settings.CustomEngageButton = "Button 9";
            Assert.Equal("Button 5", src.MouseGestureSettings[0].Settings.CustomEngageButton);
        }
    }

    /// <summary>
    /// The card's VM legs (discussion #216), per-(slot, device) scope: two
    /// mice on ONE slot keep independent Custom activation recordings, the
    /// two-devices-one-slot bar. Runs in the serialized statics collection
    /// because it swaps SettingsManager.UserSettings / UserDevices.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MouseGestureCustomActivationVmTests : IDisposable
    {
        private static readonly Guid MouseA = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        private static readonly Guid MouseB = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        private static readonly Guid Pedals = new("cccccccc-cccc-cccc-cccc-cccccccccccc");

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;

        public MouseGestureCustomActivationVmTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;

            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();

            AddMouse(MouseA, online: true);
            AddMouse(MouseB, online: false);
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
        }

        private static void AddMouse(Guid guid, bool online)
        {
            var ud = new UserDevice
            {
                InstanceGuid = guid,
                ProductName = "Test Mouse " + guid.ToString()[..2],
                CapType = InputDeviceType.Mouse,
                IsOnline = online,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);

            var us = new UserSetting { InstanceGuid = guid, MapTo = 0 };
            us.SetPadSetting(new PadSetting());
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(us);
        }

        private static void SetOnline(Guid guid, bool online)
        {
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                foreach (var d in SettingsManager.UserDevices.Items)
                    if (d.InstanceGuid == guid) d.IsOnline = online;
            }
        }

        private static PadSetting PadSettingOf(Guid guid)
        {
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                foreach (var us in SettingsManager.UserSettings.Items)
                    if (us.InstanceGuid == guid) return us.GetPadSetting();
            }
            return null;
        }

        [Fact]
        public void Two_Mice_One_Slot_Keep_Independent_Custom_Recordings()
        {
            var vm = new PadViewModel(0);

            // Mouse A is the online mouse: record a gamepad button.
            vm.LoadMouseGestureSettingsForActiveDevice();
            vm.MouseGestureButtonCustom = true;
            vm.MouseGestureCustomEngageButton = "Button 5";
            vm.MouseGestureCustomEngageDeviceGuid = Pedals.ToString();

            var entryA = Assert.Single(PadSettingOf(MouseA).MouseGestureSettings);
            Assert.Equal("Button 5", entryA.Settings.CustomEngageButton);
            Assert.Equal(Pedals.ToString(), entryA.Settings.CustomEngageDeviceGuid);
            Assert.NotEqual(0, entryA.Settings.GestureButtons & (1 << 5));
            Assert.Null(PadSettingOf(MouseB).MouseGestureSettings);

            // Switch the active mouse to B: the tab loads B's OWN state
            // (defaults, nothing recorded), not A's.
            SetOnline(MouseA, false);
            SetOnline(MouseB, true);
            vm.LoadMouseGestureSettingsForActiveDevice();
            Assert.False(vm.MouseGestureButtonCustom);
            Assert.Equal("", vm.MouseGestureCustomEngageButton);

            // Record a pedal axis on B; A's recording must be untouched.
            vm.MouseGestureButtonCustom = true;
            vm.MouseGestureCustomEngageButton = "Axis 2";
            vm.MouseGestureCustomEngageDeviceGuid = Pedals.ToString();

            var entryB = Assert.Single(PadSettingOf(MouseB).MouseGestureSettings);
            Assert.Equal("Axis 2", entryB.Settings.CustomEngageButton);
            entryA = Assert.Single(PadSettingOf(MouseA).MouseGestureSettings);
            Assert.Equal("Button 5", entryA.Settings.CustomEngageButton);

            // And switching back re-loads A's recording verbatim.
            SetOnline(MouseA, true);
            SetOnline(MouseB, false);
            vm.LoadMouseGestureSettingsForActiveDevice();
            Assert.True(vm.MouseGestureButtonCustom);
            Assert.Equal("Button 5", vm.MouseGestureCustomEngageButton);
            Assert.Equal(Pedals.ToString(), vm.MouseGestureCustomEngageDeviceGuid);
        }

        [Fact]
        public void Card_Reset_Clears_Custom_Recording()
        {
            var vm = new PadViewModel(0);
            vm.LoadMouseGestureSettingsForActiveDevice();
            vm.MouseGestureButtonCustom = true;
            vm.MouseGestureCustomEngageButton = "Button 5";
            vm.MouseGestureCustomEngageDeviceGuid = Pedals.ToString();

            vm.ResetMouseGesturesCardCommand.Execute(null);

            Assert.False(vm.MouseGestureButtonCustom);
            Assert.Equal("", vm.MouseGestureCustomEngageButton);
            Assert.Equal("", vm.MouseGestureCustomEngageDeviceGuid);
            var entry = Assert.Single(PadSettingOf(MouseA).MouseGestureSettings);
            Assert.Equal("", entry.Settings.CustomEngageButton);
            Assert.Equal(1 << 3, entry.Settings.GestureButtons);

            // The per-row reset clears only the recording pair.
            vm.MouseGestureButtonCustom = true;
            vm.MouseGestureCustomEngageButton = "Button 5";
            vm.MouseGestureCustomEngageDeviceGuid = Pedals.ToString();
            vm.ResetMouseGestureCustomEngageCommand.Execute(null);
            Assert.True(vm.MouseGestureButtonCustom);
            Assert.Equal("", vm.MouseGestureCustomEngageButton);
            Assert.Equal("", vm.MouseGestureCustomEngageDeviceGuid);
        }
    }
}
