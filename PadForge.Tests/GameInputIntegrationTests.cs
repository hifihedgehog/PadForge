using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests;

[Collection("SettingsManagerStatics")]
public class GameInputIntegrationTests
{
    [Fact]
    public void MetadataCopiesNativePropertiesWithoutReplacingTheIdentityPath()
    {
        uint props = CreateProperties();
        Assert.NotEqual(0u, props);
        GameInputDeviceMetadata metadata;
        const string identity = "0123456789ABCDEF0123456789ABCDEF";
        const string path = @"\\?\HID#VID_045E&PID_0B00#controller#{test}";
        try
        {
            Set(props, "device_id", identity);
            Set(props, "root_id", "ROOT");
            Set(props, "pnp_path", path);
            Set(props, "container_id", Guid.NewGuid().ToString());
            Set(props, "firmware", "5.23.0.0");
            Set(props, "runtime_path", @"C:\Windows\System32\GameInputRedist.dll");
            Set(props, "runtime_version", "3.3.221.0");
            Assert.True(SetNumberProperty(props, "SDL.joystick.gameinput.supported_layout", 0x3c000000));
            metadata = GameInputDeviceMetadata.Read(props);
        }
        finally { DestroyProperties(props); }
        Assert.NotNull(metadata);
        Assert.Equal(identity, metadata.DeviceId);
        Assert.Equal(path, metadata.HardwarePath(identity));
        Assert.Equal(0x3c000000u, metadata.SupportedLayout);
        Assert.Contains("runtime=3.3.221.0", metadata.DiagnosticText);
        Assert.Equal("gameinput", SdlDeviceWrapper.BackendFromGuid(new string('0', 28) + "67" + "00"));
        Guid stable = SdlDeviceWrapper.BuildInstanceGuid(identity, 0x045e, 0x0b00, 1);
        Assert.Equal(stable, SdlDeviceWrapper.BuildInstanceGuid(identity, 0x045e, 0x0b00, 99));
        Assert.NotEqual(stable, SdlDeviceWrapper.BuildInstanceGuid(metadata.NativePath, 0x045e, 0x0b00, 1));
        Assert.Equal(identity, (metadata with { NativePath = "" }).HardwarePath(identity));
    }

    [Fact]
    public void NativePathAloneDoesNotMakeAnotherBackendGameInput()
    {
        uint props = CreateProperties();
        try
        {
            Set(props, "pnp_path", @"\\?\HID#unrelated");
            Assert.Equal("managed fallback", SDL3.SDL.SDL_GetStringProperty(props, "missing", "managed fallback"));
            Assert.Null(GameInputDeviceMetadata.Read(props));
        }
        finally { DestroyProperties(props); }
        Assert.Null(GameInputDeviceMetadata.Read(0));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void PaddleAndFaceButtonReachIndependentMappingRows(int paddle)
    {
        var savedDevices = SettingsManager.UserDevices;
        var savedSettings = SettingsManager.UserSettings;
        try
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            Guid device = Guid.NewGuid();
            var physical = new UserDevice { InstanceGuid = device, IsOnline = true, CapType = InputDeviceType.Gamepad, CapButtonCount = 22 };
            SettingsManager.UserDevices.Items.Add(physical);
            SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = device, MapTo = 0 });
            var mapping = new MappingSet();
            var paddleRow = new MappingRow { Target = "ButtonA", LayerMask = "Base" };
            paddleRow.Sources.Add(new MappingSource { Kind = "Direct", DeviceGuid = device.ToString(), Descriptor = $"Button {paddle}" });
            mapping.Rows.Add(paddleRow);
            var faceRow = new MappingRow { Target = "ButtonB", LayerMask = "Base" };
            faceRow.Sources.Add(new MappingSource { Kind = "Direct", DeviceGuid = device.ToString(), Descriptor = "Button 0" });
            mapping.Rows.Add(faceRow);
            InputManager.ClearAllShiftRuntime();
            for (int mask = 0; mask < 4; ++mask)
            {
                var input = new CustomInputState();
                input.Buttons[paddle] = (mask & 1) != 0;
                input.Buttons[0] = (mask & 2) != 0;
                physical.InputState = input;
                Assert.True(InputManager.TryEvaluateMappingSetButton(input, mapping, device.ToString(), 0, "ButtonA", 50, out bool a));
                Assert.True(InputManager.TryEvaluateMappingSetButton(input, mapping, device.ToString(), 0, "ButtonB", 50, out bool b));
                Assert.Equal(input.Buttons[paddle], a);
                Assert.Equal(input.Buttons[0], b);
            }
        }
        finally { SettingsManager.UserDevices = savedDevices; SettingsManager.UserSettings = savedSettings; }
    }

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void PaddleSupportsDoublePressAndSimultaneousButtonChords(int paddle)
    {
        var savedDevices = SettingsManager.UserDevices;
        var savedSettings = SettingsManager.UserSettings;
        try
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var state = new CustomInputState();
            Guid guid = Guid.NewGuid();
            SettingsManager.UserDevices.Items.Add(new UserDevice { InstanceGuid = guid, IsOnline = true, CapType = InputDeviceType.Gamepad, CapButtonCount = 22, InputState = state });
            SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = guid, MapTo = 0 });
            var action = new MacroAction { Type = MacroActionType.ToggleVcButton, ButtonFlags = Gamepad.A };
            var macro = new MacroItem { IsEnabled = true, PadIndex = 0, TriggerMode = MacroTriggerMode.DoublePress, RepeatMode = MacroRepeatMode.Once, ConsumeTriggerButtons = false };
            macro.SetTriggerInputEntries(new List<MacroItem.TriggerInputEntry> { new() { DeviceGuid = guid, RawButton = paddle } });
            macro.Actions.Add(action);
            var manager = new InputManager();
            void Tick() { var output = new Gamepad(); manager.EvaluateSlotMacros(ref output, new[] { macro }); }
            state.Buttons[paddle] = true;
            Tick();
            Assert.False(action.VcToggleLatched);
            state.Buttons[paddle] = false;
            Tick();
            state.Buttons[paddle] = true;
            Tick();
            Assert.True(action.VcToggleLatched);

            action.VcToggleLatched = false;
            macro.TriggerMode = MacroTriggerMode.OnPress;
            macro.SetTriggerInputEntries(new List<MacroItem.TriggerInputEntry>
            {
                new() { DeviceGuid = guid, RawButton = paddle },
                new() { DeviceGuid = guid, RawButton = paddle == 15 ? 12 : paddle + 1 },
            });
            Array.Clear(state.Buttons);
            Tick();
            state.Buttons[paddle] = true;
            Tick();
            Assert.False(action.VcToggleLatched);
            state.Buttons[paddle == 15 ? 12 : paddle + 1] = true;
            Tick();
            Assert.True(action.VcToggleLatched);
        }
        finally { SettingsManager.UserDevices = savedDevices; SettingsManager.UserSettings = savedSettings; }
    }

    [Fact]
    public void BackendChangeKeepsTheEstablishedOfflineAdoptionPolicy()
    {
        var savedDevices = SettingsManager.UserDevices;
        var savedSettings = SettingsManager.UserSettings;
        (Guid Old, Guid New)[] savedPending;
        lock (InputManager.PendingDeviceGuidMigrationsLock)
        {
            savedPending = InputManager.PendingDeviceGuidMigrations.ToArray();
            InputManager.PendingDeviceGuidMigrations.Clear();
        }
        try
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            Guid product = SdlDeviceWrapper.BuildProductGuid(0x045e, 0x0b00);
            var first = new UserDevice { InstanceGuid = Guid.NewGuid(), ProductGuid = product, SerialNumber = "A" };
            var second = new UserDevice { InstanceGuid = Guid.NewGuid(), ProductGuid = product, SerialNumber = "B" };
            SettingsManager.UserDevices.Items.Add(first);
            SettingsManager.UserDevices.Items.Add(second);
            Guid firstOld = first.InstanceGuid, secondOld = second.InstanceGuid;
            var setting = new UserSetting { InstanceGuid = firstOld, MapTo = 2 };
            var pad = new PadSetting { LeftTriggerRouteActivatorDeviceGuid = firstOld.ToString() };
            setting.SetPadSetting(pad);
            SettingsManager.UserSettings.Items.Add(setting);
            var manager = new InputManager();
            Guid newA = SdlDeviceWrapper.BuildInstanceGuid("GAMEINPUT-A", 0x045e, 0x0b00, 1);
            Guid newB = SdlDeviceWrapper.BuildInstanceGuid("GAMEINPUT-B", 0x045e, 0x0b00, 2);
            Assert.Same(first, manager.FindOrCreateUserDevice(newA, product, serialNumber: "B"));
            first.IsOnline = true;
            Assert.Same(second, manager.FindOrCreateUserDevice(newB, product, serialNumber: "A"));
            second.IsOnline = true;
            Assert.Equal(newA, setting.InstanceGuid);
            Assert.Equal(2, setting.MapTo);
            Assert.Same(pad, setting.GetPadSetting());
            Assert.Contains((firstOld, newA), InputManager.PendingDeviceGuidMigrations);
            Assert.Contains((secondOld, newB), InputManager.PendingDeviceGuidMigrations);
            Assert.Same(first, manager.FindOrCreateUserDevice(newA, product));
            Assert.Same(second, manager.FindOrCreateUserDevice(newB, product));

            InputService.RemapDeviceGuidsInStoredPadSettings(firstOld, newA);
            Assert.Equal(newA.ToString(), pad.LeftTriggerRouteActivatorDeviceGuid);
            first.IsOnline = false;
            Assert.Same(first, manager.FindOrCreateUserDevice(firstOld, product));
            Assert.Equal(firstOld, setting.InstanceGuid);
        }
        finally
        {
            SettingsManager.UserDevices = savedDevices;
            SettingsManager.UserSettings = savedSettings;
            lock (InputManager.PendingDeviceGuidMigrationsLock)
            {
                InputManager.PendingDeviceGuidMigrations.Clear();
                InputManager.PendingDeviceGuidMigrations.AddRange(savedPending);
            }
        }
    }

    private static void Set(uint props, string key, string value) =>
        Assert.True(SetStringProperty(props, "SDL.joystick.gameinput." + key, value));

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_CreateProperties")]
    private static extern uint CreateProperties();
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_DestroyProperties")]
    private static extern void DestroyProperties(uint props);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_SetStringProperty")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetStringProperty(uint props, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_SetNumberProperty")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetNumberProperty(uint props, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, long value);
}
