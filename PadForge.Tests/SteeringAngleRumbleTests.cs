using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests;

[Collection("SettingsManagerStatics")]
public class SteeringAngleRumbleTests
{
    private static PadSetting Enabled(string strength = "100", string deadzone = "0") => new()
    {
        SteeringAngleRumbleEnabled = "1", SteeringAngleRumbleStrength = strength,
        SteeringAngleRumbleDeadzone = deadzone,
    };

    [Theory]
    [InlineData(-32768, 65535, 0)]
    [InlineData(32767, 0, 65535)]
    [InlineData(0, 0, 0)]
    [InlineData(-16384, 32768, 0)]
    public void FullTravelAndCenterHaveExactMotorValues(int axis, int left, int right)
    {
        SteeringAngleRumble.Compute(SteeringAngleRumble.Pack(new Gamepad { ThumbLX = (short)axis }),
            Enabled(), out var l, out var r);
        Assert.Equal(left, l);
        Assert.Equal(right, r);
    }

    /// <summary>#510: Left Motor or Right Motor takes both turns at the level the split
    /// gives each side. "0", and any value the engine does not know, splits by direction.</summary>
    [Theory]
    [InlineData("1", -32768, 65535, 0)]
    [InlineData("1", 32767, 65535, 0)]
    [InlineData("1", 0, 0, 0)]
    [InlineData("2", -32768, 0, 65535)]
    [InlineData("2", 32767, 0, 65535)]
    [InlineData("2", -16384, 0, 32768)]
    [InlineData("0", -32768, 65535, 0)]
    [InlineData("0", 32767, 0, 65535)]
    [InlineData("", 32767, 0, 65535)]
    [InlineData(null, -32768, 65535, 0)]
    [InlineData("3", 32767, 0, 65535)]
    [InlineData("-1", -32768, 65535, 0)]
    [InlineData("left", 32767, 0, 65535)]
    public void TheMotorSettingPutsBothTurnsOnOneMotorOrSplitsThem(string motor, int axis, int left, int right)
    {
        var ps = Enabled();
        ps.SteeringAngleRumbleMotor = motor;
        SteeringAngleRumble.Compute(SteeringAngleRumble.Pack(new Gamepad { ThumbLX = (short)axis }),
            ps, out var l, out var r);
        Assert.Equal(left, l);
        Assert.Equal(right, r);
    }

    [Fact]
    public void DeadzoneIsSilentAndBothDirectionsGrowMonotonicallyToTheSamePeak()
    {
        var ps = Enabled("50", "2");
        foreach (int sign in new[] { -1, 1 })
        {
            ushort prior = 0;
            for (int value = 0; value <= 32767; value++)
            {
                SteeringAngleRumble.Compute(SteeringAngleRumble.Pack(new Gamepad { ThumbLX = (short)(sign * value) }),
                    ps, out var left, out var right);
                ushort motor = sign < 0 ? left : right;
                Assert.True(motor >= prior);
                if (value <= 655) Assert.Equal(0, motor);
                Assert.Equal(0, sign < 0 ? right : left);
                prior = motor;
            }
            Assert.InRange(prior, (ushort)32766, (ushort)32768);
        }
    }

    [Fact]
    public void PackedAxesDoNotSignExtendIntoNeighbors()
    {
        var frame = SteeringAngleRumble.Pack(new Gamepad
            { ThumbLX = -1, ThumbLY = 12345, ThumbRX = short.MinValue, ThumbRY = short.MaxValue });
        Assert.Equal(new short[] { -1, 12345, short.MinValue, short.MaxValue },
            Enumerable.Range(0, 4).Select(i => SteeringAngleRumble.Axis(frame, i)));
        Assert.Equal(0, SteeringAngleRumble.Axis(frame, 4));
    }

    [Fact]
    public void MergePreservesEveryOtherFieldAndNeverChangesTheIncomingObject()
    {
        var raw = new Vibration(100, 50000)
        {
            LeftTriggerMotorSpeed = 333, RightTriggerMotorSpeed = 444,
            HasDirectionalData = true, HasConditionData = true, EffectType = 8,
            SignedMagnitude = -3000, Direction = 4000, Period = 5000, DeviceGain = 70,
            ConditionAxisCount = 1, ConditionAxes = new[] { new ConditionAxisData { Offset = 77 } },
        };
        var scratch = new Vibration();
        long frame = SteeringAngleRumble.Pack(new Gamepad { ThumbLX = short.MinValue });
        var result = SteeringAngleRumble.Merge(raw, Enabled("50"), frame, scratch);
        Assert.Same(scratch, result);
        Assert.Equal(100, raw.LeftMotorSpeed);
        Assert.Equal(32768, result.LeftMotorSpeed);
        Assert.Equal(50000, result.RightMotorSpeed);
        foreach (var p in typeof(Vibration).GetProperties().Where(p => p.Name is not "LeftMotorSpeed" and not "RightMotorSpeed"))
            Assert.Equal(p.GetValue(raw), p.GetValue(result));
        var disabled = SteeringAngleRumble.Merge(raw, new PadSetting(), frame, scratch);
        Assert.Same(raw, disabled);
        Assert.Equal(100, disabled.LeftMotorSpeed);
    }

    [Theory]
    [InlineData("0", "0", 0)]
    [InlineData("-1", "0", 0)]
    [InlineData("101", "-5", 65535)]
    [InlineData("NaN", "NaN", 32768)]
    public void InvalidNumericSettingsStayBounded(string strength, string deadzone, int expected)
    {
        SteeringAngleRumble.Compute(SteeringAngleRumble.Pack(new Gamepad { ThumbLX = short.MinValue }),
            Enabled(strength, deadzone), out var l, out var r);
        Assert.Equal(expected, l);
        Assert.Equal(0, r);
    }

    [Fact]
    public void SettingsSurviveXmlCloneChecksumAndBothViewModelSyncPairs()
    {
        using var rig = new Rig();
        var ps = Enabled("73", "7");
        ps.SteeringAngleRumbleAxis = "2";
        ps.SteeringAngleRumbleMotor = "2";
        foreach (string name in SettingNames)
        {
            var copy = ps.CloneDeep();
            Assert.Equal(typeof(PadSetting).GetProperty(name).GetValue(ps), typeof(PadSetting).GetProperty(name).GetValue(copy));
            var before = copy.ComputeChecksum();
            typeof(PadSetting).GetProperty(name).SetValue(copy, "0");
            Assert.NotEqual(before, copy.ComputeChecksum());
        }
        var serializer = new XmlSerializer(typeof(PadSetting));
        using var text = new StringWriter();
        serializer.Serialize(text, ps);
        var restored = (PadSetting)serializer.Deserialize(new StringReader(text.ToString()));
        AssertSettings(ps, restored);

        var main = new MainViewModel();
        var service = new InputService(main);
        var settings = new SettingsService(main);
        var vm = main.Pads[0];
        InputService.LoadPadSettingIntoViewModel(vm, restored);
        Invoke(service, "SaveViewModelToPadSetting", vm, rig.Device.InstanceGuid, false);
        AssertSettings(ps, rig.Assignment.GetPadSetting());

        rig.Assignment.SetPadSetting(ps);
        Invoke(settings, "LoadPadSettings", new[] { rig.Assignment }, new[] { ps });
        Assert.True(vm.SteeringAngleRumbleEnabled);
        Assert.Equal(2, vm.SteeringAngleRumbleAxis);
        Assert.Equal(73, vm.SteeringAngleRumbleStrength);
        Assert.Equal(7, vm.SteeringAngleRumbleDeadzone);
        Assert.Equal(2, vm.SteeringAngleRumbleMotor);
        settings.UpdatePadSettingsFromViewModels();
        AssertSettings(ps, rig.Assignment.GetPadSetting());
        // The Force Feedback card's Reset All resets the feature with the
        // rest of the card: its settings sit under the card's More row.
        vm.ResetForceAllCommand.Execute(null);
        Assert.False(vm.SteeringAngleRumbleEnabled);
        Assert.Equal(0, vm.SteeringAngleRumbleAxis);
        Assert.Equal(50, vm.SteeringAngleRumbleStrength);
        Assert.Equal(2, vm.SteeringAngleRumbleDeadzone);
        Assert.Equal(0, vm.SteeringAngleRumbleMotor);
        vm.SteeringAngleRumbleMotor = 1;
        vm.ResetSteeringAngleRumbleMotorCommand.Execute(null);
        Assert.Equal(0, vm.SteeringAngleRumbleMotor);
        vm.SteeringAngleRumbleEnabled = true;
        vm.SteeringAngleRumbleMotor = 2;
        vm.ResetAllSettings();
        Assert.False(vm.SteeringAngleRumbleEnabled);
        Assert.Equal(0, vm.SteeringAngleRumbleMotor);
    }

    /// <summary>The picker shows what the engine plays: a value the engine reads as split
    /// shows as Split by Direction, not as the nearest choice.</summary>
    [Fact]
    public void AMotorValueTheEngineDoesNotKnowShowsAsSplit()
    {
        using var rig = new Rig();
        var vm = new MainViewModel().Pads[0];
        vm.SteeringAngleRumbleMotor = 2;
        Assert.Equal(2, vm.SteeringAngleRumbleMotor);
        vm.SteeringAngleRumbleMotor = 7;
        Assert.Equal(0, vm.SteeringAngleRumbleMotor);
        vm.SteeringAngleRumbleMotor = -1;
        Assert.Equal(0, vm.SteeringAngleRumbleMotor);
        var ps = Enabled();
        ps.SteeringAngleRumbleMotor = "9";
        InputService.LoadPadSettingIntoViewModel(vm, ps);
        Assert.Equal(0, vm.SteeringAngleRumbleMotor);
    }

    /// <summary>The picker's position is the stored value, so its items stay in the
    /// engine's order: split, left, right.</summary>
    [Fact]
    public void TheMotorPickerListsTheChoicesInTheEnginesOrder()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SharedVersion.cs"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string xaml = File.ReadAllText(Path.Combine(dir.FullName, "PadForge.App", "Views", "PadPage.xaml"));
        int start = xaml.IndexOf("x:Name=\"SteeringAngleRumbleMotorPicker\"", StringComparison.Ordinal);
        Assert.True(start > 0, "the motor picker was not found");
        int end = xaml.IndexOf("</StackPanel>", start, StringComparison.Ordinal);
        string row = xaml[start..end];
        Assert.Contains("SelectedIndex=\"{Binding SteeringAngleRumbleMotor, Mode=TwoWay}\"", row);
        Assert.Equal(new[] { "Pad_SteeringAngleRumble_MotorSplit", "Pad_LeftMotorLabel", "Pad_RightMotorLabel" },
            Regex.Matches(row, @"<ComboBoxItem Content=""\{Binding (\w+),").Select(m => m.Groups[1].Value));
        Assert.Contains("ResetSteeringAngleRumbleMotorCommand", row);
        Assert.Contains("SettingLabel=\"{Binding Pad_SteeringAngleRumble_Motor,", row);
    }

    private static readonly string[] SettingNames = { "SteeringAngleRumbleEnabled", "SteeringAngleRumbleAxis",
        "SteeringAngleRumbleStrength", "SteeringAngleRumbleDeadzone", "SteeringAngleRumbleMotor" };
    private static void AssertSettings(PadSetting a, PadSetting b)
    {
        foreach (string name in SettingNames)
            Assert.Equal(typeof(PadSetting).GetProperty(name).GetValue(a), typeof(PadSetting).GetProperty(name).GetValue(b));
    }

    [Fact]
    public void RealFeedbackPassRampsMixesScalesAndStopsOnCenterDisableAndUnassignment()
    {
        using var rig = new Rig();
        rig.Publish(short.MinValue);
        rig.Feedback();
        Assert.Equal((ushort.MaxValue, (ushort)0), rig.Output.Sent.Last());
        rig.Manager.VibrationStates[0].RightMotorSpeed = 40000;
        rig.Publish(-16384);
        rig.Feedback();
        Assert.Equal(((ushort)32768, (ushort)40000), rig.Output.Sent.Last());
        rig.Assignment.GetPadSetting().ForceOverall = "50";
        rig.Assignment.GetPadSetting().ForceSwapMotor = "1";
        rig.Feedback();
        Assert.Equal(((ushort)20000, (ushort)16384), rig.Output.Sent.Last());
        rig.Manager.VibrationStates[0].RightMotorSpeed = 0;
        rig.Publish(0);
        rig.Feedback();
        Assert.Equal(((ushort)0, (ushort)0), rig.Output.Sent.Last());
        rig.Publish(short.MinValue);
        rig.Feedback();
        rig.Assignment.GetPadSetting().SteeringAngleRumbleEnabled = "0";
        rig.Feedback();
        Assert.Equal(((ushort)0, (ushort)0), rig.Output.Sent.Last());
        rig.Assignment.GetPadSetting().SteeringAngleRumbleEnabled = "1";
        rig.Feedback();
        SettingsManager.UserSettings.Items.Clear();
        rig.Feedback();
        Assert.Equal(((ushort)0, (ushort)0), rig.Output.Sent.Last());
        int count = rig.Output.Sent.Count;
        rig.Feedback();
        Assert.Equal(count, rig.Output.Sent.Count);
    }

    /// <summary>#510: the picked motor is named the way game rumble names it. Overall
    /// Gain, the motor strengths and Swap Left and Right Motors apply afterward, so with
    /// Swap on the picked motor swaps too.</summary>
    [Fact]
    public void ThePickedMotorTakesBothTurnsAndSwapsLikeGameRumble()
    {
        using var rig = new Rig();
        var ps = rig.Assignment.GetPadSetting();
        ps.SteeringAngleRumbleMotor = "1";
        rig.Publish(short.MaxValue);
        rig.Feedback();
        Assert.Equal((ushort.MaxValue, (ushort)0), rig.Output.Sent.Last());
        ps.ForceSwapMotor = "1";
        rig.Feedback();
        Assert.Equal(((ushort)0, ushort.MaxValue), rig.Output.Sent.Last());
        ps.ForceSwapMotor = "0";
        ps.SteeringAngleRumbleMotor = "2";
        rig.Publish(short.MinValue);
        rig.Feedback();
        Assert.Equal(((ushort)0, ushort.MaxValue), rig.Output.Sent.Last());
    }

    [Fact]
    public void SecondDeviceDoesNotInheritFirstDevicesCueAndMetersMatchThePhysicalPath()
    {
        using var rig = new Rig();
        var second = rig.AddDevice(false);
        rig.Publish(short.MinValue);
        rig.Manager.SelectedDeviceGuids[0] = second.InstanceGuid;
        rig.Manager.ComputeFinalVibrationStates();
        Assert.Equal(65535, rig.Manager.FinalVibrationStates[0].LeftMotorSpeed);
        Assert.Equal(0, rig.Manager.SelectedDeviceVibrationStates[0].LeftMotorSpeed);
        Invoke(rig.Manager, "ApplyForceFeedback", second);
        Assert.Equal(0, second.ForceFeedbackState.LeftMotorSpeed);
        rig.Manager.SelectedDeviceGuids[0] = rig.Device.InstanceGuid;
        rig.Manager.ComputeFinalVibrationStates();
        Assert.Equal(65535, rig.Manager.SelectedDeviceVibrationStates[0].LeftMotorSpeed);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("disconnected")]
    [InlineData("inactive")]
    [InlineData("stopped")]
    [InlineData("quiesced")]
    [InlineData("idle")]
    [InlineData("background")]
    [InlineData("raw")]
    public void InactiveFramesCannotReplayTheLastAngle(string reason)
    {
        using var rig = new Rig();
        rig.Publish(short.MinValue);
        Assert.NotEqual(0, rig.Manager.ReadSteeringAngleFrame(0));
        switch (reason)
        {
            case "disabled": SettingsManager.SlotEnabled[0] = false; break;
            case "deleted": SettingsManager.SlotCreated[0] = false; break;
            case "disconnected": rig.Controller.Connected = false; break;
            case "inactive": Field<int[]>(rig.Manager, "_slotInactiveCounter")[0] = 1; break;
            case "stopped": SetField(rig.Manager, "_running", false); break;
            case "quiesced": rig.Manager.OutputsQuiesced = true; break;
            case "idle": rig.Manager.IsIdle = true; break;
            case "background": rig.Manager.SuspendWhenBackground = true; rig.Manager.HostIsForeground = false; break;
            case "raw": rig.Manager.SlotControllerTypes[0] = VirtualControllerType.Extended; break;
        }
        Invoke(rig.Manager, "RetrieveOutputStates");
        Assert.Equal(0, Field<long[]>(rig.Manager, "_steeringAngleFrames")[0]);
        Assert.Equal(0, rig.Manager.ReadSteeringAngleFrame(0));
    }

    [Fact]
    public void NativeConstantForceDevicesKeepTheirExistingIdleForce()
    {
        using var rig = new Rig();
        rig.Output.NativeForce = true;
        Assert.False(InputManager.SupportsSteeringAngleRumble(rig.Device));
        rig.Publish(short.MinValue);
        var ps = rig.Assignment.GetPadSetting();
        ps.ConstantForceEnabled = "1";
        ps.ConstantForceX = "1";
        var baseline = ConstantForceEvaluator.Resolve(new Vibration(), ps, new Vibration());
        var input = rig.Manager.ResolveUserRumble(0, ps, new Vibration(), new Vibration(),
            InputManager.SupportsSteeringAngleRumble(rig.Device));
        var actual = ConstantForceEvaluator.Resolve(input, ps, new Vibration());
        Assert.Equal(baseline.LeftMotorSpeed, actual.LeftMotorSpeed);
        Assert.Equal(baseline.SignedMagnitude, actual.SignedMagnitude);
        Assert.True(actual.HasDirectionalData);
    }

    [Fact]
    public void FocusSuspensionSendsAZeroThroughTheExistingWriter()
    {
        using var rig = new Rig();
        rig.Publish(short.MinValue);
        rig.Feedback();
        rig.Manager.SuspendWhenBackground = true;
        rig.Manager.HostIsForeground = false;
        Assert.True(rig.Manager.ApplyFocusSuspension(() => { }, () => { }));
        Assert.Equal(((ushort)0, (ushort)0), rig.Output.Sent.Last());
    }

    [Fact]
    public void TwoAssignedSourcesUseTheCombinedVirtualAxis()
    {
        using var rig = new Rig();
        rig.AddDevice(false);
        rig.Assignment.OutputState = new Gamepad { ThumbLX = -8000 };
        SettingsManager.UserSettings.Items[1].OutputState = new Gamepad { ThumbLX = 24000 };
        Invoke(rig.Manager, "CombineOutputStates");
        Invoke(rig.Manager, "RetrieveOutputStates");
        rig.Feedback();
        Assert.Equal(0, rig.Output.Sent.Last().Item1);
        Assert.InRange(rig.Output.Sent.Last().Item2, (ushort)47999, (ushort)48002);
    }

    [Fact]
    public void MultipleSlotsMaxCombineAndForeignTestTargetsDoNotContribute()
    {
        using var rig = new Rig();
        SettingsManager.SlotCreated[1] = SettingsManager.SlotEnabled[1] = true;
        rig.Manager.SlotControllerTypes[1] = VirtualControllerType.Xbox;
        Field<IVirtualController[]>(rig.Manager, "_virtualControllers")[1] = new ControllerStub();
        var second = new UserSetting { InstanceGuid = rig.Device.InstanceGuid, MapTo = 1 };
        second.SetPadSetting(Enabled());
        SettingsManager.UserSettings.Items.Add(second);
        rig.Manager.CombinedOutputStates[1] = new Gamepad { ThumbLX = short.MaxValue };
        rig.Publish(short.MinValue);
        rig.Feedback();
        Assert.Equal((ushort.MaxValue, ushort.MaxValue), rig.Output.Sent.Last());
        rig.Manager.TestRumbleTargetGuid[1] = Guid.NewGuid();
        rig.Feedback();
        Assert.Equal((ushort.MaxValue, (ushort)0), rig.Output.Sent.Last());
        SettingsManager.SlotEnabled[0] = false;
        rig.Feedback();
        Assert.Equal(((ushort)0, (ushort)0), rig.Output.Sent.Last());
    }

    [Fact]
    public void NativeUnassignmentLeavesPeerOwnedOutputAlone()
    {
        using var rig = new Rig();
        rig.Publish(short.MinValue);
        rig.Feedback();
        SettingsManager.UserSettings.Items.Clear();
        RemoteLinkOutputRouter.ClaimOutput(rig.Device.DevicePath);
        int count = rig.Output.Sent.Count;
        rig.Feedback();
        Assert.Equal(count, rig.Output.Sent.Count);
        RemoteLinkOutputRouter.ReleaseDevice(rig.Device.DevicePath);
        rig.Feedback();
        Assert.Equal(((ushort)0, (ushort)0), rig.Output.Sent.Last());
    }

    [Fact]
    public void SonyTriggerRoutingUsesTheSameScaledSteeringCue()
    {
        using var rig = new Rig();
        rig.Publish(short.MinValue);
        var ps = rig.Assignment.GetPadSetting();
        ps.ForceOverall = "50";
        // The route is per (slot, device), because the twelve route fields
        // live on each device's own PadSetting. A slot-wide runtime handed
        // every device on the slot the first configured one's route.
        SeedRouteCell(rig.Manager, 0, rig.Device.InstanceGuid, srcLeft: 1, scaleLeft: 1);
        ushort left = 0, right = 0;
        rig.Manager.ApplyTriggerRoutingForSony(0, rig.Device.InstanceGuid, ps,
            new Vibration(), new Vibration(), new Vibration(), ref left, ref right);
        Assert.Equal(32767, left);
        Assert.Equal(0, right);
        rig.Feedback();
        Assert.Equal(left, rig.Output.Sent.Last().Item1);
    }

    /// <summary>Seeds one device's route cell on a slot, appending to whatever
    /// rows the slot already has. The cell type is private, so this walks the
    /// published jagged array to find it.</summary>
    private static void SeedRouteCell(InputManager mgr, int slot, Guid device,
        byte srcLeft = 0, double scaleLeft = 1, bool redirectLeft = false,
        byte srcRight = 0, double scaleRight = 1, bool redirectRight = false)
    {
        var rowsField = typeof(InputManager).GetField("_triggerRouteCfg",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(rowsField);
        var rows = (Array)rowsField.GetValue(mgr);
        Type cellType = rows.GetType().GetElementType().GetElementType();

        object cell = Activator.CreateInstance(cellType, nonPublic: true);
        void Set(string name, object value) => cellType.GetField(name).SetValue(cell, value);
        Set("Slot", slot);
        Set("Device", device);
        Set("SrcLeft", srcLeft);
        Set("ScaleLeft", scaleLeft);
        Set("RedirectLeft", redirectLeft);
        Set("EngagedLeft", srcLeft != 0);
        Set("SrcRight", srcRight);
        Set("ScaleRight", scaleRight);
        Set("RedirectRight", redirectRight);
        Set("EngagedRight", srcRight != 0);

        var existing = (Array)rows.GetValue(slot);
        int n = existing?.Length ?? 0;
        var next = Array.CreateInstance(cellType, n + 1);
        for (int i = 0; i < n; i++) next.SetValue(existing.GetValue(i), i);
        next.SetValue(cell, n);
        rows.SetValue(next, slot);
    }

    /// <summary>Two devices on one slot, each with its own route. Before the
    /// runtime was keyed by (slot, device) there was one route per slot, so the
    /// second device either inherited the first one's route or, under Redirect,
    /// lost its main motors to a setting it never carried.</summary>
    [Fact]
    public void TwoDevicesOnOneSlotEachRouteTheirOwn()
    {
        using var rig = new Rig();
        rig.Publish(short.MinValue);
        var ps = rig.Assignment.GetPadSetting();
        ps.ForceOverall = "50";
        var other = Guid.NewGuid();

        // This device routes the left main motor to the left trigger; the
        // other device on the same slot routes nothing.
        SeedRouteCell(rig.Manager, 0, rig.Device.InstanceGuid, srcLeft: 1, scaleLeft: 1);
        SeedRouteCell(rig.Manager, 0, other);

        ushort mine = 0, mineR = 0;
        rig.Manager.ApplyTriggerRoutingForSony(0, rig.Device.InstanceGuid, ps,
            new Vibration(), new Vibration(), new Vibration(), ref mine, ref mineR);
        Assert.Equal(32767, mine);

        ushort theirs = 0, theirsR = 0;
        rig.Manager.ApplyTriggerRoutingForSony(0, other, ps,
            new Vibration(), new Vibration(), new Vibration(), ref theirs, ref theirsR);
        Assert.Equal(0, theirs);
    }

    /// <summary>The Sony main-motor redirect read the slot with no owner check
    /// at all, so one device's Redirect silenced every other device's main
    /// motors on the same slot.</summary>
    [Fact]
    public void ARedirectSilencesOnlyItsOwnDevice()
    {
        using var rig = new Rig();
        rig.Publish(short.MinValue);
        var other = Guid.NewGuid();

        SeedRouteCell(rig.Manager, 0, rig.Device.InstanceGuid, srcLeft: 1, redirectLeft: true);
        SeedRouteCell(rig.Manager, 0, other);

        rig.Manager.GetTriggerRouteMainRedirect(0, rig.Device.InstanceGuid,
            out bool mineL, out bool mineR);
        Assert.True(mineL);
        Assert.False(mineR);

        rig.Manager.GetTriggerRouteMainRedirect(0, other, out bool theirsL, out bool theirsR);
        Assert.False(theirsL);
        Assert.False(theirsR);

        // And a device with no cell at all is untouched.
        rig.Manager.GetTriggerRouteMainRedirect(0, Guid.NewGuid(),
            out bool strayL, out bool strayR);
        Assert.False(strayL);
        Assert.False(strayR);
    }

    [Fact]
    public void SonySteeringWakeUsesTheExistingSharedDeviceGroup()
    {
        using var rig = new Rig();
        rig.Device.VendorId = 0x054c;
        var second = new UserSetting { InstanceGuid = rig.Device.InstanceGuid, MapTo = 1 };
        second.SetPadSetting(Enabled());
        SettingsManager.UserSettings.Items.Add(second);
        Invoke(rig.Manager, "RefreshSonyPokeCfg", SettingsManager.UserSettings);
        Assert.True(Field<bool[]>(rig.Manager, "_sonySteeringAngleEnabled")[1]);
        var config = Field<(bool audio, bool cfPoke, int shareMask)[]>(rig.Manager, "_sonyPokeCfg");
        Assert.True(InputManager.GroupNeed(1 << 1, 0, config[0].shareMask, 0).Item1);
        Assert.False(InputManager.GroupNeed(0, 0, config[0].shareMask, 0).Item1);
    }

    [Fact]
    public void RetirementClearsTheAngleWithoutAnotherPollingFrame()
    {
        using var rig = new Rig();
        rig.Publish(short.MinValue);
        Assert.NotEqual(0, rig.Manager.ReadSteeringAngleFrame(0));
        typeof(InputManager).GetMethod("DestroyVirtualController", Private, null,
            new[] { typeof(int), typeof(bool) }, null).Invoke(rig.Manager, new object[] { 0, false });
        Assert.Equal(0, rig.Manager.ReadSteeringAngleFrame(0));
        rig.Feedback();
        Assert.Empty(rig.Output.Sent);
    }

    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T Field<T>(object obj, string field) => (T)obj.GetType().GetField(field, Private).GetValue(obj);
    private static void SetField(object obj, string field, object value) => obj.GetType().GetField(field, Private).SetValue(obj, value);
    private static void Invoke(object obj, string method, params object[] args)
        => obj.GetType().GetMethod(method, Private).Invoke(obj, args);

    private sealed class ControllerStub : IVirtualController
    {
        public bool Connected = true;
        public int FeedbackPadIndex { get; set; }
        public VirtualControllerType Type => VirtualControllerType.Xbox;
        public bool IsConnected => Connected;
        public void Connect() { }
        public void Disconnect() => Connected = false;
        public void Dispose() => Connected = false;
        public void SubmitGamepadState(Gamepad state) { }
        public void RegisterFeedbackCallback(int padIndex, Vibration[] states) { }
    }

    public class OutputStub : DispatchProxy
    {
        public readonly List<(ushort, ushort)> Sent = new();
        public bool NativeForce;
        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == "SetRumble") { Sent.Add(((ushort)args[0], (ushort)args[1])); return true; }
            if (method.Name == "StopRumble") { Sent.Add((0, 0)); return true; }
            if (method.Name is "get_HasRumble" or "get_IsAttached") return true;
            if (method.Name == "get_HasHaptic") return NativeForce;
            if (method.Name == "get_HapticFeatures") return NativeForce ? (uint)SDL3.SDL.SDL_HAPTIC_CONSTANT : 0u;
            if (method.ReturnType == typeof(void)) return null;
            return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly bool[] _created = SettingsManager.SlotCreated;
        private readonly bool[] _enabled = SettingsManager.SlotEnabled;
        public readonly InputManager Manager = new();
        public readonly ControllerStub Controller = new();
        public readonly UserDevice Device;
        public readonly UserSetting Assignment;
        public OutputStub Output => (OutputStub)Device.Device;

        public Rig()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
            SettingsManager.SlotEnabled = new bool[InputManager.MaxPads];
            SettingsManager.SlotCreated[0] = SettingsManager.SlotEnabled[0] = true;
            SetField(Manager, "_running", true);
            Manager.SlotControllerTypes[0] = VirtualControllerType.Xbox;
            Field<IVirtualController[]>(Manager, "_virtualControllers")[0] = Controller;
            Device = AddDevice(true);
            Assignment = SettingsManager.UserSettings.Items[0];
        }

        public UserDevice AddDevice(bool enabled)
        {
            var device = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), DevicePath = "steering-test://" + Guid.NewGuid(), IsOnline = true,
                CapType = InputDeviceType.Gamepad, Device = DispatchProxy.Create<ISdlInputDevice, OutputStub>(),
                ForceFeedbackState = new ForceFeedbackState(),
            };
            SettingsManager.UserDevices.Items.Add(device);
            var setting = new UserSetting { InstanceGuid = device.InstanceGuid, MapTo = 0 };
            setting.SetPadSetting(enabled ? Enabled() : new PadSetting());
            SettingsManager.UserSettings.Items.Add(setting);
            return device;
        }

        public void Publish(short axis)
        {
            Manager.CombinedOutputStates[0] = new Gamepad { ThumbLX = axis };
            Invoke(Manager, "RetrieveOutputStates");
        }
        public void Feedback() => Invoke(Manager, "ApplyForceFeedback", Device);
        public void Dispose()
        {
            SetField(Manager, "_running", false);
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.SlotCreated = _created;
            SettingsManager.SlotEnabled = _enabled;
        }
    }
}
