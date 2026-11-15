using PadForge.Engine;
using PadForge.Engine.Data;
using static SDL3.SDL;
using Xunit.Abstractions;

namespace PadForge.Tests;

public sealed class HapticEffectDeliveryTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> ConditionChanges()
    {
        foreach (int axis in new[] { 0, 1 })
            foreach (string field in new[] { "positive", "negative", "offset", "deadband", "positiveSaturation", "negativeSaturation" })
                yield return [axis, field];
    }

    [Theory]
    [MemberData(nameof(ConditionChanges))]
    public void InPlaceConditionChangesReachTheNativeEffect(int axis, string field)
    {
        using var h = new HapticEffectFixture();
        var value = h.Condition();
        h.Apply(value);
        Assert.Single(h.Writes);
        Assert.True(h.Running);
        h.Apply(value);
        Assert.Single(h.Writes);
        long before = Read(h.Writes[0], axis, field);
        ref var change = ref value.ConditionAxes[axis];
        switch (field)
        {
            case "positive": change.PositiveCoefficient = 7000; break;
            case "negative": change.NegativeCoefficient = 6000; break;
            case "offset": change.Offset = 2000; break;
            case "deadband": change.DeadBand = 2000; break;
            case "positiveSaturation": change.PositiveSaturation = 8000; break;
            case "negativeSaturation": change.NegativeSaturation = 7000; break;
        }
        h.Apply(value);
        output.WriteLine($"axis={axis} field={field} acceptedWrites={h.Writes.Count}");
        Assert.Equal(2, h.Writes.Count);
        Assert.NotEqual(before, Read(h.Writes[1], axis, field));
        h.Apply(value);
        Assert.Equal(2, h.Writes.Count);
    }

    static long Read(SDL_HapticEffect effect, int axis, string field) => (axis, field) switch
    {
        (0, "positive") => effect.condition.right_coeff0,
        (1, "positive") => effect.condition.right_coeff1,
        (0, "negative") => effect.condition.left_coeff0,
        (1, "negative") => effect.condition.left_coeff1,
        (0, "offset") => effect.condition.center0,
        (1, "offset") => effect.condition.center1,
        (0, "deadband") => effect.condition.deadband0,
        (1, "deadband") => effect.condition.deadband1,
        (0, "positiveSaturation") => effect.condition.right_sat0,
        (1, "positiveSaturation") => effect.condition.right_sat1,
        (0, "negativeSaturation") => effect.condition.left_sat0,
        (1, "negativeSaturation") => effect.condition.left_sat1,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void GainChangesReachUnchangedConditionAndDirectionalEffects(bool condition, bool deviceGain)
    {
        using var h = new HapticEffectFixture();
        var value = condition ? h.Condition() : h.Directional();
        h.Apply(value);
        Assert.Single(h.Writes);
        if (deviceGain) value.DeviceGain = 128;
        else h.Settings.ForceOverall = "50";
        h.Apply(value);
        output.WriteLine($"condition={condition} deviceGain={deviceGain} acceptedWrites={h.Writes.Count}");
        Assert.Equal(2, h.Writes.Count);
        // Directional magnitude is quantized to HID units before SDL scaling.
        short expected = deviceGain ? (short)(condition ? 6579 : 6576) : (short)6553;
        short actual = condition ? h.Writes[1].condition.right_coeff0 : h.Writes[1].constant.level;
        Assert.Equal(expected, actual);
        h.Apply(value);
        Assert.Equal(2, h.Writes.Count);
    }

    [Fact]
    public void ReducingConditionAxisCountClearsTheUnusedAxis()
    {
        using var h = new HapticEffectFixture();
        var value = h.Condition();
        h.Apply(value);
        Assert.NotEqual(0, h.Writes[0].condition.right_coeff1);
        value.ConditionAxisCount = 1;
        h.Apply(value);
        Assert.Equal(2, h.Writes.Count);
        Assert.Equal(0, h.Writes[1].condition.right_coeff1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnsupportedEffectsTrackTheirScalarFallback(bool left)
    {
        using var h = new HapticEffectFixture();
        h.Set("HapticFeatures", SDL_HAPTIC_LEFTRIGHT);
        var value = h.Condition();
        value.LeftMotorSpeed = 1000;
        value.RightMotorSpeed = 1000;
        h.Apply(value);
        Assert.Equal(SDL_HAPTIC_LEFTRIGHT, h.Writes[0].type);
        if (left) value.LeftMotorSpeed = 2000;
        else value.RightMotorSpeed = 2000;
        h.Apply(value);
        Assert.Equal(2, h.Writes.Count);
        Assert.Equal(2000, left ? h.Writes[1].leftright.large_magnitude : h.Writes[1].leftright.small_magnitude);
    }

    [Fact]
    public void FailedDeliveryDoesNotAcknowledgeChangedParameters()
    {
        using var h = new HapticEffectFixture();
        var value = h.Condition();
        h.Apply(value);
        value.ConditionAxes[0].Offset = 5000;
        h.FailUpdate = true;
        h.FailCreate = true;
        h.Apply(value);
        Assert.Single(h.Writes);
        h.FailUpdate = false;
        h.FailCreate = false;
        h.Apply(value);
        Assert.Equal(2, h.Writes.Count);
        Assert.Equal(16383, h.Writes[1].condition.center0);
    }

    [Fact]
    public void CacheOwnsTheDeliveredValueWhenTheSourceChangesDuringDelivery()
    {
        using var h = new HapticEffectFixture();
        var value = h.Condition();
        h.AfterWrite = () => value.ConditionAxes[0].Offset = 5000;
        h.Apply(value);
        h.AfterWrite = null;
        h.Apply(value);
        Assert.Equal(2, h.Writes.Count);
        Assert.Equal(0, h.Writes[0].condition.center0);
        Assert.Equal(16383, h.Writes[1].condition.center0);
    }

    [Fact]
    public void EquivalentClampedGainDoesNotResendAnEffect()
    {
        using var h = new HapticEffectFixture();
        var value = h.Condition();
        h.Apply(value);
        h.Settings.ForceOverall = "999";
        h.Apply(value);
        Assert.Single(h.Writes);
    }

    [Fact]
    public void FailedReplacementCannotSuppressRestoringThePreviousEffect()
    {
        using var h = new HapticEffectFixture();
        var value = h.Condition();
        h.Apply(value);
        Assert.True(h.Running);
        value.ConditionAxes[0].Offset = 5000;
        h.FailUpdate = h.FailCreate = true;
        h.Apply(value);
        Assert.False(h.Running);
        value.ConditionAxes[0].Offset = 0;
        h.FailUpdate = h.FailCreate = false;
        h.Apply(value);
        Assert.True(h.Running);
        Assert.Equal(2, h.Writes.Count);
    }

    [Theory]
    [InlineData("magnitude")]
    [InlineData("direction")]
    [InlineData("period")]
    [InlineData("type")]
    public void DirectionalParametersStillReachTheEffect(string field)
    {
        using var h = new HapticEffectFixture();
        var value = h.Directional();
        value.EffectType = FfbEffectTypes.Sine;
        value.Period = 100;
        h.Apply(value);
        switch (field)
        {
            case "magnitude": value.SignedMagnitude = 6000; break;
            case "direction": value.Direction = 16000; break;
            case "period": value.Period = 200; break;
            case "type": value.EffectType = FfbEffectTypes.Const; break;
        }
        h.Apply(value);
        Assert.Equal(2, h.Writes.Count);
        h.Apply(value);
        Assert.Equal(2, h.Writes.Count);
    }

    [Fact]
    public void OnlyAvailableConditionAxesAreCopied()
    {
        using var h = new HapticEffectFixture();
        var value = h.Condition();
        value.ConditionAxes = [value.ConditionAxes[0]];
        h.Apply(value);
        Assert.Single(h.Writes);
        Assert.NotEqual(0, h.Writes[0].condition.right_coeff0);
        Assert.Equal(0, h.Writes[0].condition.right_coeff1);
    }
}

internal sealed class HapticEffectFixture : IDisposable
{
    internal readonly SdlDeviceWrapper Device = new();
    internal readonly ForceFeedbackState State = new();
    internal readonly PadSetting Settings = new() { ForceOverall = "100", AutoCenterStrength = "0" };
    internal readonly List<SDL_HapticEffect> Writes = [];
    internal readonly List<string> Calls = [];
    internal readonly HashSet<int> Allocated = [];
    /// <summary>Each allocated effect's type, so an update that changes the
    /// type is refused, as SDL refuses it.</summary>
    internal readonly Dictionary<int, ushort> Types = [];
    /// <summary>The effects that are playing.</summary>
    internal readonly HashSet<int> Playing = [];
    internal bool FailCreate, FailUpdate;
    internal bool FailRun = false;
    /// <summary>A create of this type fails, whatever the room.</summary>
    internal ushort? FailCreateOfType;
    /// <summary>How many effects the device holds at once. A create past it
    /// fails, as DirectInput's CreateEffect fails on a full device.</summary>
    internal int Room = int.MaxValue;
    internal Action AfterWrite;
    int nextId;

    /// <summary>True while any effect plays.</summary>
    internal bool Running => Playing.Count > 0;

    internal bool PlayingType(uint type) => Playing.Any(id => Types.TryGetValue(id, out var t) && t == type);

    internal HapticEffectFixture()
    {
        Set("Haptic", new IntPtr(1));
        Set("HapticFeatures", SDL_HAPTIC_SPRING | SDL_HAPTIC_CONSTANT | SDL_HAPTIC_SINE | SDL_HAPTIC_LEFTRIGHT);
        Set("NumHapticAxes", 2);
        Set("HapticStrategy", HapticEffectStrategy.LeftRight);
        // All native effect operations are replaced before this handle is used.
        State.CreateEffect = (IntPtr handle, ref SDL_HapticEffect effect) =>
        {
            Calls.Add("create");
            if (FailCreate || FailCreateOfType == effect.type || Allocated.Count >= Room) return -1;
            Writes.Add(effect);
            AfterWrite?.Invoke();
            Allocated.Add(++nextId);
            Types[nextId] = effect.type;
            return nextId;
        };
        State.UpdateEffect = (IntPtr handle, int id, ref SDL_HapticEffect effect) =>
        {
            Calls.Add("update");
            if (FailUpdate || !Types.TryGetValue(id, out var type) || effect.type != type) return false;
            Writes.Add(effect);
            AfterWrite?.Invoke();
            return true;
        };
        State.RunEffect = (_, id, _) =>
        {
            Calls.Add("run");
            if (FailRun) return false;
            Playing.Add(id);
            return true;
        };
        State.StopEffect = (_, id) => { Calls.Add("stop"); Playing.Remove(id); return true; };
        State.DestroyEffect = (_, id) =>
        {
            Calls.Add("destroy");
            Allocated.Remove(id);
            Playing.Remove(id);
            Types.Remove(id);
        };
    }

    internal void Set(string name, object value) => typeof(SdlDeviceWrapper).GetProperty(name)!.SetValue(Device, value);
    internal void Apply(Vibration value) => State.SetDeviceForces(null, Device, Settings, value);
    internal Vibration Condition() => new()
    {
        HasConditionData = true, EffectType = FfbEffectTypes.Spring, ConditionAxisCount = 2,
        ConditionAxes = [new() { PositiveCoefficient = 4000, NegativeCoefficient = 3000, PositiveSaturation = 10000, NegativeSaturation = 10000 },
                         new() { PositiveCoefficient = 5000, NegativeCoefficient = 2000, PositiveSaturation = 10000, NegativeSaturation = 10000 }]
    };
    internal Vibration Directional() => new()
    {
        HasDirectionalData = true, EffectType = FfbEffectTypes.Const, SignedMagnitude = 4000, Direction = 8192
    };
    public void Dispose()
    {
        State.StopDeviceForces(Device);
        Set("Haptic", IntPtr.Zero);
        Device.Dispose();
    }
}
