using System.Text.Json;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;

namespace PadForge.Tests;

public sealed class ClipboardRecognitionTests
{
    public static IEnumerable<object[]> ExportedScalars()
        => JsonSerializer.Deserialize<Dictionary<string, string>>(new PadSetting().ToJson())!
            .Where(pair => !pair.Key.StartsWith("__", StringComparison.Ordinal))
            .Select(pair => new object[] { pair.Key, pair.Value });

    [Theory]
    [MemberData(nameof(ExportedScalars))]
    public void EachExportedScalarIsRecognizedOnItsOwn(string key, string value)
    {
        // Beside the layout key every PadForge copy since 2.1.0 carries, which
        // marks the text as PadForge's own (#600) and is no content itself.
        var parsed = PadSetting.FromJson(JsonSerializer.Serialize(new Dictionary<string, string> { [key] = value, ["__OutputType"] = "0" }));
        Assert.NotNull(parsed);
        Assert.Equal(value, typeof(PadSetting).GetProperty(key)!.GetValue(parsed));
    }

    [Theory]
    [InlineData("{\"unrelatedApplication\":\"unrelatedValue\"}")]
    [InlineData("{\"__UnknownSection\":\"[]\"}")]
    [InlineData("{\"__OutputType\":\"0\",\"__IsExtended\":\"0\"}")]
    [InlineData("{\"__IsExtended\":\"1\"}")]
    [InlineData("{\"PadSettingChecksum\":\"12345678\"}")]
    [InlineData("{\"SlotMenusJson\":\"[]\"}")]
    [InlineData("{\"ButtonA_suffix\":\"Button 1\"}")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"ButtonA\":true}")]
    // A name PadForge shares with another program's preset, alone (#600).
    [InlineData("{\"ButtonA\":\"Button 1\",\"LeftThumbDeadZoneX\":\"8000\"}")]
    public void UnrelatedOrNonExportDictionariesAreRejected(string json)
        => Assert.Null(PadSetting.FromJson(json));

    [Theory]
    [InlineData("__ExtendedMappings", "RawMappingEntries")]
    [InlineData("__MidiMappings", "MidiMappingEntries")]
    [InlineData("__KbmMappings", "KbmMappingEntries")]
    [InlineData("__VrMappings", "VrMappingEntries")]
    [InlineData("__MappingDeadZones", "MappingDeadZoneEntries")]
    [InlineData("__MappingBidirectional", "MappingBidirectionalEntries")]
    [InlineData("__TouchpadSettings", "TouchpadSettings")]
    [InlineData("__MouseGestureSettings", "MouseGestureSettings")]
    [InlineData("__MultiSourceRows", "DeviceScopedMultiSourceRows")]
    [InlineData("__SlotRows", "SlotMultiSourceRows")]
    public void ExplicitEmptyTypedSectionsAreRecognized(string key, string property)
    {
        var parsed = PadSetting.FromJson(JsonSerializer.Serialize(new Dictionary<string, string> { [key] = "[]" }));
        Assert.NotNull(parsed);
        Assert.NotNull(typeof(PadSetting).GetProperty(property)!.GetValue(parsed));
    }

    [Theory]
    [InlineData("__SlotDeviceConfigs", "SlotDeviceConfigsJson", "[]")]
    [InlineData("__SlotPlayStationConfigs", "SlotDeviceConfigsJson", "[]")]
    [InlineData("__SlotExtendedConfig", "SlotExtendedConfigJson", "{}")]
    [InlineData("__SlotMidiConfig", "SlotMidiConfigJson", "{}")]
    [InlineData("__SlotKbmConfig", "SlotKbmConfigJson", "{}")]
    [InlineData("__SlotShiftActivators", "SlotShiftActivatorsJson", "{}")]
    [InlineData("__SlotMenus", "SlotMenusJson", "[]")]
    [InlineData("__SlotSetExtras", "SlotSetExtrasJson", "{}")]
    [InlineData("__SlotMacros", "SlotMacrosJson", "{\"Type\":\"PadForgeMacro\",\"Version\":1,\"Macros\":[]}")]
    [InlineData("__SlotPerDeviceSettings", "SlotPerDeviceSettingsJson", "[]")]
    public void OpaqueSectionsAndTheLegacyConfigNameRoundTrip(string key, string property, string value)
    {
        var parsed = PadSetting.FromJson(JsonSerializer.Serialize(new Dictionary<string, string> { [key] = value }));
        Assert.NotNull(parsed);
        Assert.Equal(value, typeof(PadSetting).GetProperty(property)!.GetValue(parsed));
    }

    [Fact]
    public void KnownContentKeepsLayoutMetadataAndIgnoresUnknownFields()
    {
        string json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["ButtonA"] = "Button 7", ["__OutputType"] = "2", ["__IsExtended"] = "1", ["futureField"] = "futureValue"
        });
        var parsed = PadSetting.FromJson(json, out var type, out bool extended);
        Assert.NotNull(parsed);
        Assert.Equal("Button 7", parsed.ButtonA);
        Assert.Equal(VirtualControllerType.Extended, type);
        Assert.True(extended);
    }
}

public partial class DeviceUnassignConfigLifecycleTests
{
    [Fact]
    public void ForeignNestedSettingsDoNotClearMatchedDeviceTuning()
    {
        var oldHook = SettingsService.AfterMappingSetsRefreshed;
        try
        {
            var (vm, input, _, _, _, settings) = ArrangePendingTopologyEdit();
            void Apply(string json) => input.ApplyPerDeviceSettingsToSlot(1,
                [new PerDeviceSettingsEntry { InstanceGuid = OtherGuid.ToString(), PadSettingJson = json }],
                VirtualControllerType.Xbox, false, VirtualControllerType.Xbox, false);
            Apply(new PadSetting { ForceOverall = "37" }.ToJson());
            Assert.Equal("37", settings.ForceOverall);
            Assert.Equal(37, vm.Pads[1].ForceOverallGain);
            Apply("{\"unrelatedApplication\":\"unrelatedValue\"}");
            Assert.Equal("37", settings.ForceOverall);
            Assert.Equal(37, vm.Pads[1].ForceOverallGain);
        }
        finally { SettingsService.AfterMappingSetsRefreshed = oldHook; }
    }
}
