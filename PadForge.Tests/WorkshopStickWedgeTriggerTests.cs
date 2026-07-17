using System;
using System.Collections.Generic;
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
    /// Translator v12 wedge-shaped descriptor triggers, materializer to
    /// evaluator. A TranslatedMacro hosted on a stick-as-dpad wedge carries
    /// the read's half-axis shape (half, direction, deadzone) beside its
    /// descriptor, the materializer stamps it onto the axis trigger entry
    /// (the picker spec's ax form), and the evaluator then fires once per
    /// deflection entry into that wedge, staying quiet on the opposite
    /// half. Without the stamp the entry reads the full axis and a
    /// dpad_north macro would fire on any deflection of the whole Y axis.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WorkshopStickWedgeTriggerTests : IDisposable
    {
        private static readonly Guid DevGuid = new("12121212-3434-5656-7878-9a9a9a9a9a9a");
        private const int Slot = 0; // materialized macros ride the Xbox slot

        private const int AxisCenter = 32768; // CustomInputState axes are unsigned 0..65535

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;

        public WorkshopStickWedgeTriggerTests()
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
                ProductName = "V12 Pad",
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
            var t = new TranslatedProfile { Name = "V12", NeedsXboxSlot = true };
            t.Macros.Add(translated);
            var profile = WorkshopProfileMaterializer.Materialize(t);
            return Assert.Single(profile.Macros);
        }

        [Fact]
        public void WedgeShape_LowersToHalfAxisSpec()
        {
            // dpad_north on the left stick: Y lower half at the group's
            // 30 percent inner deadzone.
            var data = Materialize(new TranslatedMacro
            {
                Name = "Tap F5 (dpad_north)",
                Action = TranslatedMacroAction.KeyTap,
                TriggerMode = "OnPress",
                VirtualKey = 0x74,
                TriggerInputDescriptors = new List<string> { "Gamepad LeftStickY" },
                TriggerDescriptorHalfAxis = true,
                TriggerDescriptorInvert = true,
                TriggerDescriptorDeadZonePercent = 30,
            });

            Assert.Equal(MacroTriggerSource.InputDevice, data.TriggerSource);
            Assert.Equal(
                "in:00000000-0000-0000-0000-000000000000:ax:LeftStickY:1:1:30:0",
                data.TriggerInputs);
        }

        [Fact]
        public void BareAxisDescriptor_WithoutShape_KeepsTheEntryDefaults()
        {
            // Control: no shape carried, the entry stays the picker default
            // (full axis, 50 percent), the pre-v12 form.
            var data = Materialize(new TranslatedMacro
            {
                Name = "Tap F5 (dpad_north)",
                Action = TranslatedMacroAction.KeyTap,
                TriggerMode = "OnPress",
                VirtualKey = 0x74,
                TriggerInputDescriptors = new List<string> { "Gamepad LeftStickY" },
            });

            Assert.Equal(
                "in:00000000-0000-0000-0000-000000000000:ax:LeftStickY:0:0:50:0",
                data.TriggerInputs);
        }

        [Fact]
        public void WedgeTrigger_FiresOncePerDeflectionEntry_AndIgnoresTheOppositeHalf()
        {
            var state = ArrangeSlotDevice();
            var im = new InputManager();

            // The observable latch action, same probe the device-free macro
            // tests use. Trigger: north wedge (LY lower half, 30 percent).
            var data = Materialize(new TranslatedMacro
            {
                Name = "Toggle B (dpad_north)",
                Action = TranslatedMacroAction.ToggleVcButton,
                TriggerMode = "OnPress",
                TargetXboxButtons = Gamepad.B,
                TriggerInputDescriptors = new List<string> { "Gamepad LeftStickY" },
                TriggerDescriptorHalfAxis = true,
                TriggerDescriptorInvert = true,
                TriggerDescriptorDeadZonePercent = 30,
            });
            var macro = SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null);
            var macros = new[] { macro };

            void Evaluate()
            {
                var gp = new Gamepad();
                im.EvaluateSlotMacros(ref gp, macros);
            }

            // Centered rest: quiet.
            state.Axis[1] = AxisCenter;
            Evaluate();
            Assert.False(macro.Actions[0].VcToggleLatched);

            // Deflect SOUTH (upper half): the opposite wedge, still quiet.
            state.Axis[1] = 60000;
            Evaluate();
            Assert.False(macro.Actions[0].VcToggleLatched);

            // Back to center, then flick NORTH past the threshold: one fire.
            state.Axis[1] = AxisCenter;
            Evaluate();
            state.Axis[1] = 5000;
            Evaluate();
            Assert.True(macro.Actions[0].VcToggleLatched);

            // Held inside the wedge: no re-fire (OnPress is edge-shaped).
            state.Axis[1] = 4000;
            Evaluate();
            Assert.True(macro.Actions[0].VcToggleLatched);

            // Re-center re-arms, the next flick fires again.
            state.Axis[1] = AxisCenter;
            Evaluate();
            state.Axis[1] = 5000;
            Evaluate();
            Assert.False(macro.Actions[0].VcToggleLatched); // latch flipped a second time
        }
    }
}
