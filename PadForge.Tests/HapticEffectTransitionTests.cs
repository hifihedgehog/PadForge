using PadForge.Engine;
using static SDL3.SDL;
using Xunit.Abstractions;

namespace PadForge.Tests;

public sealed class HapticEffectTransitionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void ReturningToScalarOutputReplacesTheDirectionalEffect(bool condition, bool nonzero)
    {
        using var h = new HapticEffectFixture();
        var value = condition ? h.Condition() : h.Directional();
        ushort left = nonzero ? (ushort)1000 : (ushort)0;
        value.LeftMotorSpeed = left;
        h.Apply(value);
        Assert.True(h.Running);
        Assert.True(h.State.IsActive);
        h.Apply(new Vibration(left, 0));
        output.WriteLine($"condition={condition} scalar={left} running={h.Running} calls={string.Join(',', h.Calls)}");
        if (nonzero)
        {
            Assert.Equal(2, h.Writes.Count);
            Assert.Equal(SDL_HAPTIC_LEFTRIGHT, h.Writes[1].type);
            Assert.Equal(left, h.Writes[1].leftright.large_magnitude);
            Assert.True(h.Running);
        }
        else
        {
            Assert.False(h.Running);
            Assert.False(h.State.IsActive);
            Assert.Contains("stop", h.Calls);
            Assert.Contains("destroy", h.Calls);
        }
        int calls = h.Calls.Count;
        h.Apply(new Vibration(left, 0));
        Assert.Equal(calls, h.Calls.Count);
        h.Apply(value);
        Assert.True(h.Running);
        Assert.True(h.State.IsActive);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConfiguredAutoCenterReplacesTheGameEffect(bool condition)
    {
        using var h = new HapticEffectFixture();
        h.Set("JoystickType", SDL_JoystickType.SDL_JOYSTICK_TYPE_WHEEL);
        h.Settings.AutoCenterStrength = "25";
        h.Apply(condition ? h.Condition() : h.Directional());
        Assert.True(h.Running);
        h.Apply(new Vibration());
        Assert.Equal(2, h.Writes.Count);
        Assert.Equal(SDL_HAPTIC_SPRING, h.Writes[1].type);
        Assert.Equal(8191, h.Writes[1].condition.right_coeff0);
        Assert.True(h.Running);
        int count = h.Calls.Count;
        h.Apply(new Vibration());
        Assert.Equal(count, h.Calls.Count);
        h.Settings.AutoCenterStrength = "0";
        h.Apply(new Vibration());
        Assert.False(h.Running);
    }

    [Fact]
    public void ExplicitStopClearsTheCacheAndAllowsTheSameEffectAgain()
    {
        using var h = new HapticEffectFixture();
        var value = h.Condition();
        h.Apply(value);
        h.State.StopDeviceForces(h.Device);
        Assert.False(h.Running);
        h.Apply(value);
        Assert.True(h.Running);
        Assert.Equal(2, h.Writes.Count);
    }

    [Fact]
    public void FailedScalarReplacementRemainsPendingAtTheSameMotorValues()
    {
        using var h = new HapticEffectFixture();
        var value = h.Condition();
        value.LeftMotorSpeed = 1000;
        h.Apply(value);
        Assert.True(h.Running);
        h.FailUpdate = h.FailCreate = true;
        h.Apply(new Vibration(1000, 0));
        Assert.False(h.Running);
        h.FailUpdate = h.FailCreate = false;
        h.Apply(new Vibration(1000, 0));
        Assert.True(h.Running);
        Assert.Equal(SDL_HAPTIC_LEFTRIGHT, h.Writes[^1].type);
    }

    /// <summary>#572: the spring joins rumble that is already playing and
    /// leaves it alone, and turning the spring off leaves it playing.</summary>
    [Fact]
    public void AutoCenterJoinsAndLeavesUnchangedScalarMotors()
    {
        using var h = new HapticEffectFixture();
        h.Set("JoystickType", SDL_JoystickType.SDL_JOYSTICK_TYPE_WHEEL);
        var scalar = new Vibration(1000, 0);
        h.Apply(scalar);
        Assert.Equal(SDL_HAPTIC_LEFTRIGHT, h.Writes[^1].type);
        int rumbleWrites = h.Writes.Count(w => w.type == SDL_HAPTIC_LEFTRIGHT);
        h.Settings.AutoCenterStrength = "25";
        h.Apply(scalar);
        Assert.Equal(SDL_HAPTIC_SPRING, h.Writes[^1].type);
        Assert.True(h.PlayingType(SDL_HAPTIC_SPRING));
        Assert.True(h.PlayingType(SDL_HAPTIC_LEFTRIGHT));
        h.Settings.AutoCenterStrength = "0";
        h.Apply(scalar);
        Assert.False(h.PlayingType(SDL_HAPTIC_SPRING));
        Assert.True(h.PlayingType(SDL_HAPTIC_LEFTRIGHT));
        Assert.Equal(rumbleWrites, h.Writes.Count(w => w.type == SDL_HAPTIC_LEFTRIGHT));
        Assert.Equal(1000, h.State.LeftMotorSpeed);
    }

    /// <summary>A device that holds one effect keeps the pre-#572 order: the
    /// spring displaces rumble, and turning it off restores the unchanged
    /// motors.</summary>
    [Fact]
    public void DisablingAutoCenterRestoresUnchangedScalarMotorsOnAOneEffectDevice()
    {
        using var h = new HapticEffectFixture { Room = 1 };
        h.Set("JoystickType", SDL_JoystickType.SDL_JOYSTICK_TYPE_WHEEL);
        var scalar = new Vibration(1000, 0);
        h.Apply(scalar);
        Assert.Equal(SDL_HAPTIC_LEFTRIGHT, h.Writes[^1].type);
        h.Settings.AutoCenterStrength = "25";
        h.Apply(scalar);
        Assert.Equal(SDL_HAPTIC_SPRING, h.Writes[^1].type);
        Assert.True(h.PlayingType(SDL_HAPTIC_SPRING));
        Assert.False(h.PlayingType(SDL_HAPTIC_LEFTRIGHT));
        h.Settings.AutoCenterStrength = "0";
        h.Apply(scalar);
        Assert.True(h.Running);
        Assert.Equal(SDL_HAPTIC_LEFTRIGHT, h.Writes[^1].type);
        Assert.Equal(1000, h.Writes[^1].leftright.large_magnitude);
    }
}
