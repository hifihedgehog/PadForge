using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests;

/// <summary>
/// The type bar that a sidebar or Dashboard card's type chip opens runs one
/// type-change path, MainWindow.ChangeSlotType (discussion #543). The two
/// cards' seven tiles had their own copies, and the Dashboard's never passed
/// the slot's Extended layout to the automap. A switch to an unlettered
/// Extended profile, such as the default Custom, then authored the standard
/// gamepad fields its raw surface never reads, and the slot came up with no
/// automap (CreateDefaultPadSetting's extended parameter).
/// </summary>
// Builds an InputService, which reaches the statics the AudioProfileLifecycle
// collection keeps apart from every other class (see its definition).
[Collection("AudioProfileLifecycle")]
public sealed class SlotTypeChangeTests
{
    static readonly string[] SlotOrderProperties =
    {
        "XboxSlotOrder", "PlayStationSlotOrder", "ExtendedSlotOrder",
        "KeyboardMouseSlotOrder", "MidiSlotOrder", "NintendoSlotOrder", "VrSlotOrder"
    };

    [Fact]
    public void ASwitchToCustomExtendedAutomapsTheRawSurface()
    {
        Exception error = null;
        var thread = new Thread(() =>
        {
            try { SwitchAnXboxSlotToExtended(); } catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(25)), "The type change did not finish.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    static void SwitchAnXboxSlotToExtended()
    {
        var oldSettings = SettingsManager.UserSettings;
        var oldDevices = SettingsManager.UserDevices;
        var oldSets = SettingsManager.SlotMappingSets;
        var oldCreated = SettingsManager.SlotCreated;
        var oldEnabled = SettingsManager.SlotEnabled;
        var oldAfter = SettingsService.AfterMappingSetsRefreshed;
        var orders = SlotOrderProperties
            .Select(name => typeof(SettingsManager).GetProperty(name)!)
            .Select(property => (Property: property, Value: property.GetValue(null)))
            .ToList();
        InputService input = null;
        try
        {
            foreach (var (property, _) in orders) property.SetValue(null, new List<int>());
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotMappingSets = Enumerable.Range(0, 16).Select(_ => new MappingSet()).ToArray();
            SettingsManager.SlotCreated = new bool[16];
            SettingsManager.SlotEnabled = new bool[16];
            SettingsManager.SlotCreated[0] = SettingsManager.SlotEnabled[0] = true;

            // A gamepad-shaped device, so the automap has something to bind.
            var id = Guid.NewGuid();
            var objects = new List<DeviceObjectItem>();
            for (int i = 0; i < 6; i++)
                objects.Add(new DeviceObjectItem { InputIndex = i, ObjectType = DeviceObjectTypeFlags.AbsoluteAxis });
            for (int i = 0; i < 12; i++)
                objects.Add(new DeviceObjectItem { InputIndex = i, ObjectType = DeviceObjectTypeFlags.PushButton });
            objects.Add(new DeviceObjectItem { InputIndex = 0, ObjectType = DeviceObjectTypeFlags.PointOfViewController });
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = id, ProductGuid = id, ProductName = "Test pad", InstanceName = "Test pad",
                CapType = (int)InputDeviceType.Gamepad, IsOnline = true, IsEnabled = true,
                DeviceObjects = objects.ToArray(),
            });
            var setting = new UserSetting { InstanceGuid = id, ProductGuid = id, MapTo = 0, IsEnabled = true };
            var blank = new PadSetting();
            blank.UpdateChecksum();
            setting.SetPadSetting(blank);
            SettingsManager.UserSettings.Items.Add(setting);

            var vm = new MainViewModel();
            var settings = new SettingsService(vm);
            input = new InputService(vm) { SettingsService = settings };
            vm.Pads[0].OutputType = VirtualControllerType.Xbox;

            var window = (PadForge.MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(PadForge.MainWindow));
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(PadForge.MainWindow).GetField("_viewModel", fields)!.SetValue(window, vm);
            typeof(PadForge.MainWindow).GetField("_settingsService", fields)!.SetValue(window, settings);
            typeof(PadForge.MainWindow).GetField("_inputService", fields)!.SetValue(window, input);
            typeof(PadForge.MainWindow).GetMethod("ChangeSlotType", fields)!.Invoke(window,
                [0, VirtualControllerType.Extended]);

            Assert.Equal(VirtualControllerType.Extended, vm.Pads[0].OutputType);
            Assert.Equal(HMaestroProfileCatalog.CustomProfileId, vm.Pads[0].ProfileId);
            var authored = setting.GetPadSetting();
            Assert.False(string.IsNullOrEmpty(authored.GetRawMapping("RawBtn0")),
                "The switch authored no raw automap for an unlettered Extended profile.");
        }
        finally
        {
            if (input != null)
            {
                PadForge.Resources.Strings.Strings.CultureChanged -=
                    (Action)Delegate.CreateDelegate(typeof(Action), input,
                        typeof(InputService).GetMethod("OnCultureChanged", BindingFlags.Instance | BindingFlags.NonPublic)!);
                input.Dispose();
            }
            foreach (var (property, value) in orders) property.SetValue(null, value);
            SettingsManager.UserSettings = oldSettings;
            SettingsManager.UserDevices = oldDevices;
            SettingsManager.SlotMappingSets = oldSets;
            SettingsManager.SlotCreated = oldCreated;
            SettingsManager.SlotEnabled = oldEnabled;
            SettingsService.AfterMappingSetsRefreshed = oldAfter;
        }
    }
}
