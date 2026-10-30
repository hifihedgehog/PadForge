using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests;

/// <summary>
/// A slot's flame on its Dashboard card follows the slot's on/off switch
/// while the input engine is stopped (discussion #543, point 1). The card
/// reads its slot summary, which the engine's display timer refreshes, so a
/// toggle made with the engine stopped changed the sidebar card and left the
/// Dashboard card showing the old state until the engine started. Every on/off
/// path now ends in MainWindow.RefreshSlotPowerViews, which refreshes both.
/// </summary>
// Builds an InputService, which reaches the statics the AudioProfileLifecycle
// collection keeps apart from every other class (see its definition).
[Collection("AudioProfileLifecycle")]
public sealed class SlotPowerToggleTests
{
    static readonly string[] SlotOrderProperties =
    {
        "XboxSlotOrder", "PlayStationSlotOrder", "ExtendedSlotOrder",
        "KeyboardMouseSlotOrder", "MidiSlotOrder", "NintendoSlotOrder", "VrSlotOrder"
    };

    [Fact]
    public void TheDashboardCardFollowsAToggleWithTheEngineStopped()
    {
        Exception error = null;
        var thread = new Thread(() =>
        {
            try { ToggleASlotWithNoEngine(); } catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(25)), "The toggle did not finish.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    static void ToggleASlotWithNoEngine()
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
            SettingsManager.XboxSlotOrder.Add(0);
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotMappingSets = Enumerable.Range(0, 16).Select(_ => new MappingSet()).ToArray();
            SettingsManager.SlotCreated = new bool[16];
            SettingsManager.SlotEnabled = new bool[16];
            SettingsManager.SlotCreated[0] = SettingsManager.SlotEnabled[0] = true;

            var vm = new MainViewModel();
            var settings = new SettingsService(vm);
            input = new InputService(vm) { SettingsService = settings };
            vm.Pads[0].OutputType = VirtualControllerType.Xbox;
            // The Dashboard card for slot 0, as UpdatePadDeviceInfo builds it.
            // No engine runs, so nothing else refreshes it.
            vm.Dashboard.RefreshActiveSlots(new List<int> { 0 }, true);
            input.RefreshSlotSummaryProperties();
            var card = vm.Dashboard.SlotSummaries.Single(s => s.PadIndex == 0);
            Assert.True(card.IsEnabled);

            var window = (PadForge.MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(PadForge.MainWindow));
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(PadForge.MainWindow).GetField("_viewModel", fields)!.SetValue(window, vm);
            typeof(PadForge.MainWindow).GetField("_settingsService", fields)!.SetValue(window, settings);
            typeof(PadForge.MainWindow).GetField("_inputService", fields)!.SetValue(window, input);
            typeof(PadForge.MainWindow).GetField("_deviceService", fields)!.SetValue(window, new DeviceService(vm, settings));
            var toggle = typeof(PadForge.MainWindow).GetMethod("OnSidebarPowerToggle", fields)!;

            // The sidebar flame, the path the reporter used.
            toggle.Invoke(window, [new System.Windows.Controls.Button { Tag = 0 }, new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)]);
            Assert.False(SettingsManager.SlotEnabled[0]);
            Assert.False(card.IsEnabled, "The Dashboard card kept the slot on after it was turned off.");

            toggle.Invoke(window, [new System.Windows.Controls.Button { Tag = 0 }, new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)]);
            Assert.True(SettingsManager.SlotEnabled[0]);
            Assert.True(card.IsEnabled, "The Dashboard card kept the slot off after it was turned on.");
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
