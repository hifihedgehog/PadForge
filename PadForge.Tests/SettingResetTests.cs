using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using PadForge.Common.Input;
using PadForge.Controls;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests;

[Collection("SettingsManagerStatics")]
public class SettingResetTests
{
    private sealed record ResetBinding(string File, string Id, string Owner, string Property);

    [Theory]
    [InlineData("ResetAudioMirrorCommand", "DeviceConfig.AudioPassthroughEnabled")]
    [InlineData("ResetEqCommand", "DeviceConfig.AudioEqEnabled")]
    [InlineData("ResetLimiterCommand", "DeviceConfig.AudioLimiterEnabled")]
    [InlineData("ResetRangeCommand", "DeadZone")]
    [InlineData("DeviceConfig.ResetLeftRangeCommand", "DeviceConfig.LeftStartPosition")]
    [InlineData("DeviceConfig.ResetRightRangeCommand", "DeviceConfig.RightStartPosition")]
    [InlineData("ResetCellsCommand", "CellCount")]
    [InlineData("ResetCellCommand", "BindingKind")]
    [InlineData("ResetMouseGestureButtonCommand", "MouseGestureButtonLeft")]
    [InlineData("ResetCommand", "FrequencyHz")]
    public void ExistingResetRowsKeepOneResetAction(string command, string input)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "PadForge.App"))) root = root.Parent;
        Assert.NotNull(root);
        var document = XDocument.Load(Path.Combine(root.FullName, "PadForge.App", "Views", "PadPage.xaml"));
        var reset = Assert.Single(document.Descendants(), n => (string)n.Attribute("Command") == "{Binding " + command + "}");
        var row = reset.Parent;
        Assert.Contains(row.Descendants().Attributes(), a => a.Value.StartsWith("{Binding " + input + ",", StringComparison.Ordinal)
            || a.Value == "{Binding " + input + "}");
        Assert.Single(row.Descendants(), n => n.Name.LocalName == "SettingResetButton"
            || ((string)n.Attribute("Style"))?.Contains("ResetButton", StringComparison.Ordinal) == true);
        Assert.False(string.IsNullOrWhiteSpace((string)reset.Attribute("ToolTip")));
    }

    [Fact]
    public void AudioRowResetsUseTheSelectedConfigurationAndKeepTheirScope()
    {
        var vm = new PadViewModel(0);
        var previous = vm.DeviceConfig;
        previous.AudioPassthroughEnabled = true;
        previous.AudioLimiterEnabled = false;
        previous.AudioEqEnabled = true;
        var mirrorReset = vm.ResetAudioMirrorCommand;
        var limiterReset = vm.ResetLimiterCommand;
        var eqReset = vm.ResetEqCommand;
        var cfg = new DeviceSlotConfig();
        vm.DeviceConfig = cfg;
        cfg.AudioPassthroughEnabled = true;
        cfg.AudioLimiterEnabled = false;
        cfg.AudioLimiterCeiling = 75;
        cfg.AudioEqEnabled = true;
        cfg.AudioEqPreampDb = 6;
        vm.AddEqBandCommand.Execute(null);
        Assert.NotEmpty(vm.EqBands);
        Assert.NotEmpty(cfg.AudioEqBands);

        mirrorReset.Execute(null);
        Assert.False(cfg.AudioPassthroughEnabled);
        Assert.False(cfg.AudioLimiterEnabled);
        Assert.True(cfg.AudioEqEnabled);
        limiterReset.Execute(null);
        Assert.True(cfg.AudioLimiterEnabled);
        Assert.Equal(75, cfg.AudioLimiterCeiling);
        Assert.True(cfg.AudioEqEnabled);
        eqReset.Execute(null);
        Assert.False(cfg.AudioEqEnabled);
        Assert.Equal(0, cfg.AudioEqPreampDb);
        Assert.Empty(vm.EqBands);
        Assert.Empty(cfg.AudioEqBands);
        Assert.Equal(75, cfg.AudioLimiterCeiling);
        Assert.True(previous.AudioPassthroughEnabled);
        Assert.False(previous.AudioLimiterEnabled);
        Assert.True(previous.AudioEqEnabled);
    }

    [Fact]
    public void RangeRowResetsCoverBothRepresentationsAndPreserveOtherSettings()
    {
        var trigger = new TriggerConfigItem(0, "Left Trigger") { DeadZone = 18, MaxRange = 80, AntiDeadZone = 7 };
        trigger.ResetRangeCommand.Execute(null);
        Assert.Equal(0, trigger.DeadZone);
        Assert.Equal(0, trigger.DeadZoneDigit);
        Assert.Equal(100, trigger.MaxRange);
        Assert.Equal(65535, trigger.MaxRangeDigit);
        Assert.Equal(7, trigger.AntiDeadZone);

        var cfg = new DeviceSlotConfig { LeftStartPosition = 40, LeftEndPosition = 90,
            RightStartPosition = 50, RightEndPosition = 100, LeftStrength = 150, RightStrength = 160 };
        cfg.ResetLeftRangeCommand.Execute(null);
        Assert.Equal(0, cfg.LeftStartPosition);
        Assert.Equal(255, cfg.LeftEndPosition);
        Assert.Equal(50, cfg.RightStartPosition);
        Assert.Equal(100, cfg.RightEndPosition);
        cfg.ResetRightRangeCommand.Execute(null);
        Assert.Equal(0, cfg.RightStartPosition);
        Assert.Equal(255, cfg.RightEndPosition);
        Assert.Equal(150, cfg.LeftStrength);
        Assert.Equal(160, cfg.RightStrength);
    }

    [Fact]
    public void EveryGenericResetBindingUsesItsOwnersWhitelist()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "PadForge.App"))) root = root.Parent;
        Assert.NotNull(root);
        var fixture = JsonSerializer.Deserialize<ResetBinding[]>(File.ReadAllText(Path.Combine(root.FullName, "PadForge.Tests", "ResetControlBindings.json")));
        Assert.NotEmpty(fixture);
        var documents = fixture.Select(x => x.File).Distinct().ToDictionary(x => x,
            x => XDocument.Load(Path.Combine(root.FullName, x)));
        foreach (var row in fixture)
        {
            var button = Assert.Single(documents[row.File].Descendants(), n =>
                (string)n.Attribute("AutomationProperties.AutomationId") == row.Id);
            Assert.Equal(row.Property, (string)button.Attribute("CommandParameter"));
            Assert.Contains("ResetSettingCommand", (string)button.Attribute("Command"));
            var owner = typeof(PadViewModel).Assembly.GetType(row.Owner);
            Assert.NotNull(owner);
            var whitelist = owner.GetMethod("CanResetSetting", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(whitelist);
            Assert.True((bool)whitelist.Invoke(null, new object[] { row.Property }), row.Id);
        }
        int actual = documents.Values.Sum(doc => doc.Descendants().Count(n => n.Name.LocalName == "SettingResetButton"
            && ((string)n.Attribute("Command"))?.EndsWith("ResetSettingCommand}", StringComparison.Ordinal) == true));
        Assert.Equal(fixture.Length, actual);
    }

    [Fact]
    public void DeviceResetUsesTheExplicitPersistenceNotificationAndInvalidatesThePreview()
    {
        var vm = new DevicesViewModel();
        var device = new DeviceRowViewModel
        {
            InstanceGuid = Guid.NewGuid(), ForceRawJoystickMode = true,
            ConsumeInputEnabled = true, IdleDisconnectMinutes = 8,
        };
        vm.SelectedDevice = device;
        vm.LastRawStateDeviceGuid = device.InstanceGuid;
        Guid notified = Guid.Empty;
        vm.DeviceHidingChanged += (_, guid) => notified = guid;
        vm.ResetSelectedDeviceSettingCommand.Execute(nameof(DeviceRowViewModel.ForceRawJoystickMode));
        Assert.False(device.ForceRawJoystickMode);
        Assert.True(device.ConsumeInputEnabled);
        Assert.Equal(8, device.IdleDisconnectMinutes);
        Assert.Equal(device.InstanceGuid, notified);
        Assert.Equal(Guid.Empty, vm.LastRawStateDeviceGuid);
    }

    [Fact]
    public void DeviceSelectionRequeriesResetAvailability()
    {
        var vm = new DevicesViewModel();
        int changes = 0;
        var command = vm.ResetSelectedDeviceSettingCommand;
        command.CanExecuteChanged += (_, _) => changes++;
        Assert.False(command.CanExecute(nameof(DeviceRowViewModel.QuickChargeEnabled)));
        vm.SelectedDevice = new DeviceRowViewModel { InstanceGuid = Guid.NewGuid() };
        Assert.True(command.CanExecute(nameof(DeviceRowViewModel.QuickChargeEnabled)));
        Assert.True(changes > 0);
        vm.SelectedDevice = null;
        Assert.False(command.CanExecute(nameof(DeviceRowViewModel.QuickChargeEnabled)));
    }

    [Fact]
    public void AResetRefreshesAlreadyDefaultValuesWithoutChangingOtherFields()
    {
        var vm = new SettingsViewModel { StartAtLogin = true, MinimizeToTray = true, StartMinimized = false };
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.ResetSettingCommand.Execute(nameof(SettingsViewModel.StartMinimized));
        Assert.Contains(nameof(SettingsViewModel.StartMinimized), changed);
        Assert.True(vm.StartAtLogin);
        Assert.True(vm.MinimizeToTray);
        Assert.False(vm.StartMinimized);
        Assert.DoesNotContain(nameof(SettingsViewModel.StartAtLogin), changed);
    }

    [Fact]
    public void MappingInputResetRetainsTheOtherInputAndItsTuning()
    {
        var source = new MappingSourceItem
        {
            Kind = "Incremental", ParamUp = "Button 0", ParamDown = "Button 1", ParamRate = 2.5,
        };
        source.ResetSettingCommand.Execute(nameof(MappingSourceItem.ParamUpInputChoice));
        Assert.Equal("", source.ParamUp);
        Assert.Equal("Button 1", source.ParamDown);
        Assert.Equal("Incremental", source.Kind);
        Assert.Equal(2.5, source.ParamRate);
    }

    [Fact]
    public void MacroResetUsesTheSavedValueAndPreservesOtherActionParameters()
    {
        var action = new MacroAction { Type = MacroActionType.PlaySound, SoundVolume = 18, SoundLoop = true, SoundFilePath = "sound.wav" };
        action.ResetSettingCommand.Execute(nameof(MacroAction.SoundVolume));
        Assert.Equal(100, action.SoundVolume);
        Assert.True(action.SoundLoop);
        Assert.Equal("sound.wav", action.SoundFilePath);
        Assert.Equal(MacroActionType.PlaySound, action.Type);
    }

    [Fact]
    public void AMenuPositionResetRaisesItsWriteThroughEventWithoutResettingTheOtherCoordinate()
    {
        var data = new MenuDefinitionEntry { PosXPercent = 20, PosYPercent = 80, OpacityPercent = 42 };
        var menu = new MenuEditorItem(data);
        int changes = 0;
        menu.Changed += () => changes++;
        menu.ResetSettingCommand.Execute(nameof(MenuEditorItem.PosXPercent));
        Assert.Equal(50, data.PosXPercent);
        Assert.Equal(80, data.PosYPercent);
        Assert.Equal(42, data.OpacityPercent);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ProfileShortcutResetPersistsThroughItsExistingCallback()
    {
        var data = new GlobalMacroData { SwitchMode = SwitchProfileMode.Specific, TargetProfileId = "kept", TriggerDeviceGuid = Guid.NewGuid() };
        int saves = 0;
        var vm = new ProfileShortcutViewModel(data, _ => { }, _ => saves++);
        var guid = data.TriggerDeviceGuid;
        vm.ResetSettingCommand.Execute(nameof(ProfileShortcutViewModel.SwitchMode));
        Assert.Equal(SwitchProfileMode.Next, data.SwitchMode);
        Assert.Equal("kept", data.TargetProfileId);
        Assert.Equal(guid, data.TriggerDeviceGuid);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void CollectionResetRefreshesHeldCheckboxOptions()
    {
        var action = new MacroAction();
        var expected = action.LightbarCycleModesCsv;
        var options = action.CycleModeOptions;
        action.LightbarCycleModesCsv = "";
        int changes = 0;
        foreach (var option in options) option.PropertyChanged += (_, e) => { if (e.PropertyName == "IsChecked") changes++; };
        action.ResetLightbarCycleModesCommand.Execute(null);
        Assert.Equal(expected, action.LightbarCycleModesCsv);
        Assert.Equal(options.Count, changes);
        Assert.Contains(options, o => o.IsChecked);
    }

    [Fact]
    public void ButtonSelectionResetClearsBothRepresentationsWithoutChangingTheAction()
    {
        var action = new MacroAction { ButtonFlags = 0xffff, DurationMs = 120, CustomButtons = "FFFFFFFF,0,0,0" };
        action.ResetButtonSelectionCommand.Execute(null);
        Assert.Equal(0, action.ButtonFlags);
        Assert.False(action.HasCustomButtons);
        Assert.Equal(120, action.DurationMs);
    }

    [Fact]
    public void ADefaultLayoutCanBeComputedWithoutMutatingTheLiveConfiguration()
    {
        var vm = new PadViewModel(0) { OutputType = VirtualControllerType.Nintendo, ProfileId = InputManager.DefaultNintendoProfileId };
        vm.ExtendedConfig.ButtonCount = 30;
        var target = new ExtendedSlotConfig { TriggerCount = 0, ThumbstickCount = 0 };
        vm.SeedExtendedConfigFromProfile(target);
        Assert.Equal(MacroButtonNames.NintendoLetteredCountFor(InputManager.DefaultNintendoProfileId), target.ButtonCount);
        Assert.Equal(30, vm.ExtendedConfig.ButtonCount);
    }

    [Theory]
    [InlineData("Reset {0}", "Gain:", "Reset Gain")]
    [InlineData("{0}をリセット", "名前：", "名前をリセット")]
    [InlineData("Réinitialiser {0}", "Profil…", "Réinitialiser Profil")]
    public void ResetTooltipUsesTheLocalizedLabelAndFormat(string format, string label, string expected)
        => Assert.Equal(expected, SettingResetButton.FormatTooltip(format, label));
}
