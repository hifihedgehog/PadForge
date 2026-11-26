using System.Text.Json;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Tests;

/// <summary>
/// The slot's Paste takes only text PadForge's own Copy wrote (#600). Every
/// release's copy carries every name of that release's copy list, empty or
/// not, and nine of them have stood in the list since 1.0.0 and in no other
/// program's preset. A copy from 2.1.0 on also carries __OutputType. Each
/// older shape below is the key list that release's ToJson wrote, read from
/// its tag. Another program's preset that shares names such as ButtonA gets
/// "Clipboard does not contain valid PadForge settings." and changes
/// nothing.
/// </summary>
public sealed class ClipboardOwnCopyTests
{
    // v1.0.0's CopyablePropertyNames, in full: 52 names and no "__" key.
    private static readonly string[] V100 =
    {
        "AxisToButtonThreshold", "ButtonA", "ButtonB", "ButtonBack", "ButtonGuide", "ButtonStart", "ButtonX", "ButtonY",
        "DPad", "DPadDown", "DPadLeft", "DPadRight", "DPadUp", "ForceOverall", "ForceSwapMotor", "ForceType",
        "LeftMotorDirection", "LeftMotorPeriod", "LeftMotorStrength", "LeftShoulder", "LeftThumbAntiDeadZone",
        "LeftThumbAntiDeadZoneX", "LeftThumbAntiDeadZoneY", "LeftThumbAxisX", "LeftThumbAxisXInvert", "LeftThumbAxisY",
        "LeftThumbAxisYInvert", "LeftThumbButton", "LeftThumbDeadZoneX", "LeftThumbDeadZoneY", "LeftThumbLinear",
        "LeftTrigger", "LeftTriggerAntiDeadZone", "LeftTriggerDeadZone", "RightMotorDirection", "RightMotorPeriod",
        "RightMotorStrength", "RightShoulder", "RightThumbAntiDeadZone", "RightThumbAntiDeadZoneX", "RightThumbAntiDeadZoneY",
        "RightThumbAxisX", "RightThumbAxisXInvert", "RightThumbAxisY", "RightThumbAxisYInvert", "RightThumbButton",
        "RightThumbDeadZoneX", "RightThumbDeadZoneY", "RightThumbLinear", "RightTrigger", "RightTriggerAntiDeadZone",
        "RightTriggerDeadZone",
    };

    // v2.0.0's: 74 names, and __VJoyMappings or __MidiMappings only when the
    // slot had such rows.
    private static readonly string[] V200 =
    {
        "AxisToButtonThreshold", "ButtonA", "ButtonB", "ButtonBack", "ButtonGuide", "ButtonStart", "ButtonX", "ButtonY",
        "DPad", "DPadDown", "DPadLeft", "DPadRight", "DPadUp", "ForceOverall", "ForceSwapMotor", "ForceType",
        "LeftMotorStrength", "LeftShoulder", "LeftThumbAntiDeadZone", "LeftThumbAntiDeadZoneX", "LeftThumbAntiDeadZoneY",
        "LeftThumbAxisX", "LeftThumbAxisXInvert", "LeftThumbAxisXNeg", "LeftThumbAxisY", "LeftThumbAxisYInvert",
        "LeftThumbAxisYNeg", "LeftThumbButton", "LeftThumbCenterOffsetX", "LeftThumbCenterOffsetY", "LeftThumbDeadZoneShape",
        "LeftThumbDeadZoneX", "LeftThumbDeadZoneY", "LeftThumbLinear", "LeftThumbMaxRangeX", "LeftThumbMaxRangeXNeg",
        "LeftThumbMaxRangeY", "LeftThumbMaxRangeYNeg", "LeftThumbSensitivityCurveX", "LeftThumbSensitivityCurveY",
        "LeftTrigger", "LeftTriggerAntiDeadZone", "LeftTriggerDeadZone", "LeftTriggerMaxRange", "LeftTriggerSensitivityCurve",
        "RightMotorStrength", "RightShoulder", "RightThumbAntiDeadZone", "RightThumbAntiDeadZoneX", "RightThumbAntiDeadZoneY",
        "RightThumbAxisX", "RightThumbAxisXInvert", "RightThumbAxisXNeg", "RightThumbAxisY", "RightThumbAxisYInvert",
        "RightThumbAxisYNeg", "RightThumbButton", "RightThumbCenterOffsetX", "RightThumbCenterOffsetY", "RightThumbDeadZoneShape",
        "RightThumbDeadZoneX", "RightThumbDeadZoneY", "RightThumbLinear", "RightThumbMaxRangeX", "RightThumbMaxRangeXNeg",
        "RightThumbMaxRangeY", "RightThumbMaxRangeYNeg", "RightThumbSensitivityCurveX", "RightThumbSensitivityCurveY",
        "RightTrigger", "RightTriggerAntiDeadZone", "RightTriggerDeadZone", "RightTriggerMaxRange", "RightTriggerSensitivityCurve",
    };

    private static string Copy(IEnumerable<string> names, params (string Key, string Value)[] more)
    {
        var dict = new Dictionary<string, string>();
        foreach (string name in names) dict[name] = "";
        dict["ButtonA"] = "Button 0";
        dict["LeftThumbDeadZoneX"] = "12";
        foreach (var (key, value) in more) dict[key] = value;
        return JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
    }

    public static IEnumerable<object[]> OlderCopies() => new[]
    {
        new object[] { "1.0.0", Copy(V100) },
        new object[] { "2.0.0", Copy(V200) },
        new object[] { "2.0.0 with vJoy rows", Copy(V200, ("__VJoyMappings", "[{\"Key\":\"Button1\",\"Value\":\"Button 3\"}]")) },
        new object[] { "2.0.0 with MIDI rows", Copy(V200, ("__MidiMappings", "[{\"Key\":\"Cc1\",\"Value\":\"Axis 0\"}]")) },
        new object[] { "2.1.0", Copy(V200, ("__OutputType", "0"), ("__IsCustomVJoy", "0")) },
    };

    /// <summary>A copy shaped as each older release wrote it pastes, with
    /// its values.</summary>
    [Theory]
    [MemberData(nameof(OlderCopies))]
    public void AnOlderPadForgesCopy_Pastes(string release, string json)
    {
        var parsed = PadSetting.FromJson(json);
        Assert.True(parsed != null, release);
        Assert.Equal("Button 0", parsed.ButtonA);
        Assert.Equal("12", parsed.LeftThumbDeadZoneX);
    }

    /// <summary>This release's copy pastes, and carries the nine names every
    /// release's copy carried.</summary>
    [Fact]
    public void ThisReleasesCopy_Pastes_AndCarriesTheNamesEveryReleaseWrote()
    {
        string json = new PadSetting { ButtonA = "Button 0" }.ToJson(VirtualControllerType.Xbox, false);
        var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!.Keys;
        Assert.Contains("__OutputType", keys);
        foreach (string name in PadSetting.OwnCopyNames)
        {
            Assert.Contains(name, keys);
            Assert.Contains(name, V100);
            Assert.Contains(name, V200);
        }
        Assert.Equal(9, PadSetting.OwnCopyNames.Count);
        Assert.Equal("Button 0", PadSetting.FromJson(json)!.ButtonA);
    }

    /// <summary>Another program's preset, every value a string and some
    /// names PadForge's own, is not a PadForge copy: its bindings would bind
    /// nothing and its tuning would land in PadForge's units.</summary>
    [Fact]
    public void AnotherProgramsPreset_IsRefused()
    {
        string preset = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["PadSettingChecksum"] = "6c1fb8b5-3c79-4d0b-9e6e-2f0d4d7a1c11",
            ["ButtonA"] = "1", ["ButtonB"] = "2", ["DPad"] = "p1", ["GamePadType"] = "1",
            ["LeftThumbAxisX"] = "a1", ["LeftThumbAxisY"] = "-2", ["LeftThumbDeadZoneX"] = "8000",
            ["LeftTriggerDeadZone"] = "20", ["LeftThumbLinearX"] = "0", ["ForceEnable"] = "1", ["ForceOverall"] = "100",
        });
        Assert.Null(PadSetting.FromJson(preset));
        Assert.Null(PadSetting.FromJson(preset, out _, out _));
    }
}
