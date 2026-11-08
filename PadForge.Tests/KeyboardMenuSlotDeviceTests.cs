using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A keyboard menu (#566) against the controllers PadForge knows: which
    /// axes a row's list splits into halves, what a row says about a
    /// controller that is no longer on the slot, where its inputs go when the
    /// menu is copied to another slot, and what the Types Through list calls
    /// the Keyboard + Mouse slots. Beside it, the Mappings grid's button rows,
    /// whose list leaves out what a button read cannot press.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class KeyboardMenuSlotDeviceTests : IDisposable
    {
        private const string Pad = "11111111-2222-3333-4444-555555555555";
        private const string Stick = "66666666-2222-3333-4444-555555555555";
        private const string Stick2 = "77777777-2222-3333-4444-555555555555";

        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();

        public KeyboardMenuSlotDeviceTests()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
        }

        public void Dispose()
        {
            SettingsManager.UserDevices = _devices;
            SettingsManager.UserSettings = _settings;
            SettingsManager.SlotMappingSets = _sets;
            Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
        }

        private static UserDevice Add(string guid, int capType, string name, Guid product = default)
        {
            var ud = new UserDevice
            {
                InstanceGuid = Guid.Parse(guid), CapType = capType, ProductName = name, InstanceName = name,
                ProductGuid = product,
            };
            SettingsManager.UserDevices.Items.Add(ud);
            return ud;
        }

        private static void Assign(UserDevice ud, int slot)
        {
            var us = new UserSetting { InstanceGuid = ud.InstanceGuid, ProductGuid = ud.ProductGuid, MapTo = slot };
            us.SetPadSetting(new PadSetting());
            SettingsManager.UserSettings.Items.Add(us);
        }

        private static MenuEditorItem Keyboard() => new(KeyboardMenuDefaults.Create(1, "Keyboard"));

        private static KeyboardInputRow Row(MenuEditorItem e, KeyboardControlAction a)
            => e.KeyboardControlRows.Single(r => r.Action == a);

        private static List<string> Offered(KeyboardInputRow row, string guid)
            => row.ChoicesView.SourceCollection.Cast<KeyboardChoice>()
                .Where(c => string.Equals(c.Input.DeviceGuid, guid, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Input.Descriptor).ToList();

        // ── A row's list ──

        [Fact]
        public void AGamepadsTrigger_IsListedWhole_AndAStickAxisWithItsHalves()
        {
            Add(Pad, InputDeviceType.Gamepad, "Pad");
            Add(Stick, InputDeviceType.Joystick, "Stick");
            var e = Keyboard();
            e.InputChoicesProvider = () => new[]
            {
                new InputChoice { Descriptor = "Axis 0", DisplayName = "Left Stick X", DeviceGuid = Pad, DeviceLabel = "Pad" },
                new InputChoice { Descriptor = "Axis 2", DisplayName = "Left Trigger", DeviceGuid = Pad, DeviceLabel = "Pad" },
                new InputChoice { Descriptor = "Axis 2", DisplayName = "Throttle", DeviceGuid = Stick, DeviceLabel = "Stick" },
            };
            foreach (var row in new[] { Row(e, KeyboardControlAction.TypeKey), Row(e, KeyboardControlAction.SteerUp), e.OpenRow })
            {
                // A trigger rests at zero: its lower half would read pressed at rest.
                Assert.Equal(new[] { "Axis 0", "HAxis 0", "IHAxis 0", "Axis 2" }, Offered(row, Pad));
                // A joystick's axis rests at the middle, so both halves stay.
                Assert.Equal(new[] { "Axis 2", "HAxis 2", "IHAxis 2" }, Offered(row, Stick));
            }
        }

        // ── A controller that left the slot ──

        [Fact]
        public void AnInputOnAControllerNotOnTheSlot_NamesTheController()
        {
            Add(Stick, InputDeviceType.Joystick, "Flight Stick");
            var e = Keyboard();
            e.InputChoicesProvider = () => new[]
            {
                new InputChoice { Descriptor = "Button 3", DisplayName = "Fire", DeviceGuid = Pad, DeviceLabel = "Pad" },
            };
            var type = Row(e, KeyboardControlAction.TypeKey);
            Assert.True(e.TryApplyKeyboardPress(type, new RecorderService.RecordedPress("Button 3", Stick, "", "")));
            string missing = string.Format(Strings.Get("Menu_Keyboard_DeviceNotOnSlot_Format"), "Flight Stick");
            Assert.Equal(missing, type.Subtitle);
            Assert.Contains(type.ChoicesView.SourceCollection.Cast<KeyboardChoice>(),
                c => c.Input.Descriptor == "Button 3" && c.Input.DeviceGuid == Stick && c.DeviceLabel == missing);

            // A device-free input reads on any device, and an input on a
            // controller on the slot names that controller.
            type.ResetInputCommand.Execute(null);
            Assert.Equal(Strings.Instance.Mapping_AnyDevice, type.Subtitle);
            Assert.True(e.TryApplyKeyboardPress(type, new RecorderService.RecordedPress("Button 3", Pad, "", "")));
            Assert.Equal("Pad", type.Subtitle);
        }

        // ── Copy From Slot and Paste ──

        /// <summary>Slot 1 and slot 2 each hold one unit of the same flight
        /// stick, and slot 1 has a keyboard menu whose Steer Right, a
        /// combination's second input and Opens With read on its unit.</summary>
        private static MenuDefinitionEntry SetUpTwoSticks()
        {
            var product = Guid.NewGuid();
            Assign(Add(Stick, InputDeviceType.Joystick, "Stick", product), 1);
            Assign(Add(Stick2, InputDeviceType.Joystick, "Stick", product), 2);
            var menu = KeyboardMenuDefaults.Create(1, "Keyboard");
            KeyboardMenuDefaults.Set(menu, KeyboardLayout.Grid, KeyboardControlAction.SteerRight,
                new KeyboardInput("HAxis 0", Stick, "Button 1", Stick));
            KeyboardMenuDefaults.SetOpenInput(menu, new KeyboardInput("Button 2", Stick, "Button 3", Stick));
            SettingsManager.SlotMappingSets[1] = new MappingSet();
            SettingsManager.SlotMappingSets[1].Menus.Add(menu);
            SettingsManager.SlotMappingSets[2] = new MappingSet();
            return menu;
        }

        private static void AssertOnSecondStick(MenuDefinitionEntry copied)
        {
            var steer = KeyboardMenuDefaults.Effective(copied, KeyboardLayout.Grid, KeyboardControlAction.SteerRight);
            Assert.Equal(new KeyboardInput("HAxis 0", Stick2, "Button 1", Stick2), steer);
            Assert.Equal(new KeyboardInput("Button 2", Stick2, "Button 3", Stick2), KeyboardMenuDefaults.OpenInput(copied));
        }

        [Fact]
        public void CopyFromSlot_MovesAKeyboardsInputsToTheTargetSlotsController()
        {
            var source = SetUpTwoSticks();
            InputService.ReplaceSlotMappingSet(2, 1);
            AssertOnSecondStick(SettingsManager.SlotMappingSets[2].Menus.Single());
            // The source keeps its own.
            Assert.Equal(Stick, KeyboardMenuDefaults.OpenInput(source).DeviceGuid);
        }

        [Fact]
        public void PastingMenus_MovesAKeyboardsInputsToTheTargetSlotsController()
        {
            SetUpTwoSticks();
            InputService.ApplyMenusSnapshotJson(2, InputService.BuildMenusSnapshotJson(1));
            AssertOnSecondStick(SettingsManager.SlotMappingSets[2].Menus.Single());
        }

        // ── Types Through's captions ──

        [Fact]
        public void RenumberingTheSlots_RefreshesTheTypesThroughList()
        {
            var vm = new MainViewModel();
            SettingsManager.SlotCreated[3] = true;
            vm.Pads[3].OutputType = VirtualControllerType.KeyboardMouse;
            vm.Pads[3].SlotLabel = "Virtual Controller 4";
            var item = Keyboard();
            item.KbmSlotsProvider = vm.Pads[0].KbmSlotChoices;
            vm.Pads[0].Menus.Add(item);
            Assert.Contains(item.TypesThroughOptions, o => o.Value == "slot:3" && o.Label.Contains("Virtual Controller 4"));

            var raised = new List<string>();
            item.PropertyChanged += (_, a) => raised.Add(a.PropertyName);
            vm.Pads[3].SlotLabel = "Virtual Controller 2";
            Assert.Contains(nameof(MenuEditorItem.TypesThroughOptions), raised);
            Assert.Contains(item.TypesThroughOptions, o => o.Value == "slot:3" && o.Label.Contains("Virtual Controller 2"));
        }

        // ── The Mappings grid's button rows ──

        [Fact]
        public void AButtonRowsList_LeavesOutAFingersPlace_AndAPositionRowKeepsIt()
        {
            var ud = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), CapType = InputDeviceType.Gamepad, ProductName = "Pad", InstanceName = "Pad",
                IsOnline = true, InputState = new CustomInputState(),
            };
            SettingsManager.UserDevices.Items.Add(ud);
            var vm = new MainViewModel();
            var svc = new InputService(vm) { SettingsService = new SettingsService(vm) };
            var pad = vm.Pads[0];
            pad.OutputType = VirtualControllerType.PlayStation;
            typeof(InputService).GetMethod("PopulateAvailableInputs", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(svc, new object[] { pad, ud });

            static bool FingerPlace(InputChoice c) => c.Descriptor == "Touchpad 0 Finger 0 X" || c.Descriptor == "Touchpad 0 Finger 0 Y";
            Assert.Contains(pad.SlotAvailableInputs, FingerPlace);
            var button = pad.Mappings.First(m => m.TargetSettingName == "ButtonA");
            var dpad = pad.Mappings.First(m => m.TargetSettingName == "DPadUp");
            var stick = pad.Mappings.First(m => m.TargetSettingName == "LeftThumbAxisX");
            foreach (var row in new[] { button, dpad })
            {
                Assert.DoesNotContain(row.AvailableInputs, FingerPlace);
                // A press the read takes is still there, and the modifier
                // picker keeps the slot's full list.
                Assert.Contains(row.AvailableInputs, c => c.Descriptor == "Touchpad 0 Finger 0 Down");
                Assert.Same(pad.SlotAvailableInputs, row.ParamInputs);
            }
            Assert.Contains(stick.AvailableInputs, FingerPlace);
            var touchX = pad.Mappings.FirstOrDefault(m => m.IsTouchpadAxisTarget);
            if (touchX != null) Assert.Contains(touchX.AvailableInputs, FingerPlace);
        }
    }
}
