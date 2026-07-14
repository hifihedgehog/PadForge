using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Wave 3 materializer proofs for the device-free InputDevice macro
    /// triggers: a TranslatedMacro carrying TriggerInputDescriptors lowers
    /// to MacroData with TriggerSource=InputDevice and picker-identical
    /// empty-guid trigger specs, loads through the real settings loader
    /// into the macro editor's shapes (the (Any device) chips), and fires
    /// through the real slot evaluator against the slot's device. This is
    /// the end-to-end for the NoDeviceFreeTrigger conversions: paddle,
    /// touchpad Down (windowed included), gesture spots, and the AND pair.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WorkshopDeviceFreeMacroTests : IDisposable
    {
        private static readonly Guid DevGuid = new("77777777-8888-9999-aaaa-bbbbbbbbbbbb");

        /// <summary>Materialized macros ride slot 0 (the Xbox slot sits
        /// first), so the arranged device maps there. No static engine
        /// caches are touched by the bool reads these tests drive, so the
        /// low slot index cannot collide with the delta-tracker tests.</summary>
        private const int Slot = 0;

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;

        public WorkshopDeviceFreeMacroTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
        }

        private static CustomInputState ArrangeSlotDevice()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();

            var state = new CustomInputState();
            var ud = new UserDevice
            {
                InstanceGuid = DevGuid,
                ProductName = "Wave3 Pad",
                IsOnline = true,
                InputState = state,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(
                    new UserSetting { InstanceGuid = DevGuid, MapTo = Slot });
            return state;
        }

        private static MacroData Materialize(TranslatedMacro translated)
        {
            var t = new TranslatedProfile { Name = "W3" };
            t.NeedsXboxSlot = true;
            t.Macros.Add(translated);
            var profile = WorkshopProfileMaterializer.Materialize(t);
            return Assert.Single(profile.Macros);
        }

        // ── The lowered DTO shape ──

        [Fact]
        public void PaddleDescriptor_LowersToInputDeviceRawButtonSpec()
        {
            var data = Materialize(new TranslatedMacro
            {
                Name = "Warp cursor (button_back_right)",
                Action = TranslatedMacroAction.MoveMouseToScreenPosition,
                TriggerMode = "OnPress",
                TriggerInputDescriptors = new List<string> { "Gamepad Paddle1" },
            });

            Assert.Equal(MacroTriggerSource.InputDevice, data.TriggerSource);
            // "Gamepad Paddle1" folds to raw Button 12, empty guid = the
            // device on the slot: the exact spec the picker would build.
            Assert.Equal("in:00000000-0000-0000-0000-000000000000:btn:12", data.TriggerInputs);
            Assert.Equal(0, (int)data.TriggerButtons);
            Assert.Null(data.TriggerAxisTargets);
            Assert.False(data.ConsumeTriggerButtons);
        }

        [Fact]
        public void ClickPlusSpotPair_LowersToAndedSpecs()
        {
            var data = Materialize(new TranslatedMacro
            {
                Name = "Toggle E (click)",
                Action = TranslatedMacroAction.ToggleKey,
                TriggerMode = "OnPress",
                VirtualKey = 0x45,
                TriggerInputDescriptors = new List<string>
                { "Touchpad 0 Click", "Touchpad 0 TouchLeft" },
            });

            Assert.Equal(MacroTriggerSource.InputDevice, data.TriggerSource);
            Assert.Equal(
                "in:00000000-0000-0000-0000-000000000000:btn:16"
                + "|in:00000000-0000-0000-0000-000000000000:tg:Touchpad 0 TouchLeft",
                data.TriggerInputs);
        }

        [Fact]
        public void WindowedDown_LowersToDescriptorSpec()
        {
            var data = Materialize(new TranslatedMacro
            {
                Name = "Turbo A (touch)",
                Action = TranslatedMacroAction.RepeatVcButtonWhileHeld,
                TriggerMode = "WhileHeld",
                TargetXboxButtons = Gamepad.A,
                IntervalMs = 40,
                TriggerInputDescriptors = new List<string> { "Touchpad 0 Finger 0 Down Right" },
            });

            Assert.Equal(MacroTriggerSource.InputDevice, data.TriggerSource);
            Assert.Equal(
                "in:00000000-0000-0000-0000-000000000000:sd:Touchpad 0 Finger 0 Down Right",
                data.TriggerInputs);
        }

        [Fact]
        public void OutputControllerMacros_KeepTheWave2Shape()
        {
            var data = Materialize(new TranslatedMacro
            {
                Name = "Tap E (button_a)",
                Action = TranslatedMacroAction.KeyTap,
                TriggerMode = "OnPress",
                VirtualKey = 0x45,
                TriggerXboxButtons = Gamepad.A,
                ConsumeTrigger = true,
            });

            Assert.Equal(MacroTriggerSource.OutputController, data.TriggerSource);
            Assert.Equal(Gamepad.A, data.TriggerButtons);
            Assert.Null(data.TriggerInputs);
            Assert.True(data.ConsumeTriggerButtons);
        }

        [Fact]
        public void RegionClampPair_BothMembersCarryTheDescriptorTrigger()
        {
            var t = new TranslatedProfile { Name = "W3", NeedsXboxSlot = true };
            t.Macros.Add(new TranslatedMacro
            {
                Name = "Cursor region (left_trackpad)",
                Action = TranslatedMacroAction.MouseLimitRegion,
                TriggerMode = "WhileHeld",
                RegionScalePercent = 25,
                TriggerInputDescriptors = new List<string> { "Touchpad 0 Finger 0 Down" },
            });
            var profile = WorkshopProfileMaterializer.Materialize(t);

            Assert.Equal(2, profile.Macros.Length);
            Assert.All(profile.Macros, d =>
            {
                Assert.Equal(MacroTriggerSource.InputDevice, d.TriggerSource);
                Assert.Equal(
                    "in:00000000-0000-0000-0000-000000000000:sd:Touchpad 0 Finger 0 Down",
                    d.TriggerInputs);
            });
            Assert.Equal(MacroTriggerMode.OnPress, profile.Macros[0].TriggerMode);
            Assert.Equal(MacroTriggerMode.OnRelease, profile.Macros[1].TriggerMode);
        }

        // ── Editor round-trip: the loaded macro carries (Any device) chips ──

        [Fact]
        public void LoadedMacro_RoundTripsTheEditorShapes()
        {
            ArrangeSlotDevice(); // display resolvers walk the statics
            var data = Materialize(new TranslatedMacro
            {
                Name = "Toggle E (click)",
                Action = TranslatedMacroAction.ToggleKey,
                TriggerMode = "OnPress",
                VirtualKey = 0x45,
                TriggerInputDescriptors = new List<string> { "Gamepad Paddle1" },
            });
            var macro = SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null);

            Assert.Equal(MacroTriggerSource.InputDevice, macro.TriggerSource);
            var entry = Assert.Single(macro.GetTriggerInputEntries());
            Assert.Equal(Guid.Empty, entry.DeviceGuid);
            Assert.Equal(12, entry.RawButton);
            Assert.True(macro.UsesRawTrigger);

            // The chip and summary render through the (Any device) sentinel.
            string sentinel = PadForge.Resources.Strings.Strings.Instance.Mapping_AnyDevice;
            Assert.Contains(sentinel, macro.TriggerDisplayText);
            Assert.StartsWith(sentinel + ":", macro.TriggerInputItems.First().Label);

            // And the spec survives a second DTO round-trip unchanged.
            Assert.Equal(data.TriggerInputs, macro.TriggerInputs);
        }

        // ── End-to-end: materialized shape fires through the evaluator ──

        [Fact]
        public void WindowedDownTrigger_FiresOnTheSlotDevice_EndToEnd()
        {
            var state = ArrangeSlotDevice();
            var pad = new TouchpadInputState(1);
            state.Touchpads = new[] { pad };
            var im = new InputManager();

            var data = Materialize(new TranslatedMacro
            {
                Name = "Toggle ButtonB (touch)",
                Action = TranslatedMacroAction.ToggleVcButton,
                TriggerMode = "OnPress",
                TargetXboxButtons = Gamepad.B,
                TriggerInputDescriptors = new List<string> { "Touchpad 0 Finger 0 Down Right" },
            });
            var macro = SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null);
            var macros = new[] { macro };

            // Finger down on the LEFT half: outside the window, quiet.
            pad.FingerDown[0] = true;
            pad.FingerX[0] = 0.25f;
            var gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(macro.Actions[0].VcToggleLatched);

            // Finger crosses into the right half: the latch fires.
            pad.FingerX[0] = 0.75f;
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(macro.Actions[0].VcToggleLatched);
        }
    }
}
