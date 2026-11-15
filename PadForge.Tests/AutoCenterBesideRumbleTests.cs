using PadForge.Engine;
using static SDL3.SDL;
using Xunit.Abstractions;

namespace PadForge.Tests;

/// <summary>
/// #572: a generic FFB wheel holds its auto-center spring in an effect of its
/// own, so rumble plays on top of it, the way FFBTestTool runs a background
/// spring beside its selectable effect (FfbDevice.cs, the main and spring
/// slots). The game's own force and Constant Force still take the wheel from
/// the spring (#282). A device that turns out to hold one effect keeps the
/// spring and lets rumble wait, as every device did before.
/// </summary>
public sealed class AutoCenterBesideRumbleTests(ITestOutputHelper output)
{
    static HapticEffectFixture Wheel(int room = int.MaxValue)
    {
        var h = new HapticEffectFixture { Room = room };
        h.Set("JoystickType", SDL_JoystickType.SDL_JOYSTICK_TYPE_WHEEL);
        h.Settings.AutoCenterStrength = "25";
        return h;
    }

    [Fact]
    public void RumbleStartsBesideAHeldSpring_AndStopsWithoutIt()
    {
        using var h = Wheel();
        h.Apply(new Vibration());
        Assert.True(h.PlayingType(SDL_HAPTIC_SPRING));
        Assert.True(h.State.IsActive);
        int springWrites = h.Writes.Count(w => w.type == SDL_HAPTIC_SPRING);

        h.Apply(new Vibration(30000, 0));
        output.WriteLine($"calls={string.Join(',', h.Calls)}");
        Assert.True(h.PlayingType(SDL_HAPTIC_SPRING));
        Assert.True(h.PlayingType(SDL_HAPTIC_LEFTRIGHT));
        Assert.Equal(2, h.Allocated.Count);
        Assert.Equal(springWrites, h.Writes.Count(w => w.type == SDL_HAPTIC_SPRING));
        Assert.Equal(30000, h.State.LeftMotorSpeed);

        h.Apply(new Vibration());
        Assert.True(h.PlayingType(SDL_HAPTIC_SPRING));
        Assert.False(h.PlayingType(SDL_HAPTIC_LEFTRIGHT));
        Assert.Single(h.Allocated);
        Assert.True(h.State.IsActive);
    }

    [Fact]
    public void SpringStrengthChanges_LeaveTheRumbleEffectAlone()
    {
        using var h = Wheel();
        h.Apply(new Vibration(30000, 0));
        int rumbleWrites = h.Writes.Count(w => w.type == SDL_HAPTIC_LEFTRIGHT);
        h.Settings.AutoCenterStrength = "50";
        h.Apply(new Vibration(30000, 0));
        Assert.Equal(16383, h.Writes[^1].condition.right_coeff0);
        Assert.Equal(rumbleWrites, h.Writes.Count(w => w.type == SDL_HAPTIC_LEFTRIGHT));
        Assert.True(h.PlayingType(SDL_HAPTIC_LEFTRIGHT));
        Assert.True(h.PlayingType(SDL_HAPTIC_SPRING));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GameForce_ReleasesTheSpringBeforeItsOwnEffect_AndCenteringReturnsAfter(bool condition)
    {
        // Constant Force arrives the same way, as directional data, so it and
        // centering stay alternatives on this path (#282).
        using var h = Wheel();
        h.Apply(new Vibration());
        Assert.True(h.PlayingType(SDL_HAPTIC_SPRING));
        h.Calls.Clear();

        h.Apply(condition ? h.Condition() : h.Directional());
        output.WriteLine($"condition={condition} calls={string.Join(',', h.Calls)}");
        // One effect left, the game's: a game spring is a spring too, so the
        // count tells the two apart, not the type.
        Assert.Single(h.Allocated);
        Assert.Single(h.Playing);
        Assert.True(h.Calls.IndexOf("destroy") < h.Calls.IndexOf("create"));

        h.Apply(new Vibration());
        Assert.True(h.PlayingType(SDL_HAPTIC_SPRING));
        Assert.Single(h.Allocated);
    }

    [Fact]
    public void GameForce_TakesAOneEffectDevice_FromTheSpring()
    {
        using var h = Wheel(room: 1);
        h.Apply(new Vibration());
        Assert.True(h.PlayingType(SDL_HAPTIC_SPRING));
        h.Apply(h.Directional());
        Assert.True(h.PlayingType(SDL_HAPTIC_CONSTANT));
        Assert.False(h.PlayingType(SDL_HAPTIC_SPRING));
        h.Apply(new Vibration());
        Assert.True(h.PlayingType(SDL_HAPTIC_SPRING));
        Assert.Single(h.Allocated);
    }

    [Fact]
    public void OneEffectDevice_SpringFirst_KeepsTheEffect_AndRumbleWaits()
    {
        using var h = Wheel(room: 1);
        h.Apply(new Vibration());
        h.Apply(new Vibration(30000, 0));
        output.WriteLine($"calls={string.Join(',', h.Calls)}");
        Assert.True(h.PlayingType(SDL_HAPTIC_SPRING));
        Assert.False(h.PlayingType(SDL_HAPTIC_LEFTRIGHT));
        Assert.Equal(0, h.State.LeftMotorSpeed);
        Assert.True(h.State.IsActive);

        int calls = h.Calls.Count;
        h.Apply(new Vibration(30000, 0));
        h.Apply(new Vibration(20000, 0));
        Assert.Equal(calls, h.Calls.Count);
    }

    [Fact]
    public void OneEffectDevice_RumbleFirst_GivesTheEffectToTheSpring()
    {
        using var h = Wheel(room: 1);
        h.Settings.AutoCenterStrength = "0";
        h.Apply(new Vibration(30000, 0));
        Assert.True(h.PlayingType(SDL_HAPTIC_LEFTRIGHT));

        h.Settings.AutoCenterStrength = "25";
        h.Apply(new Vibration(30000, 0));
        Assert.True(h.PlayingType(SDL_HAPTIC_SPRING));
        Assert.False(h.PlayingType(SDL_HAPTIC_LEFTRIGHT));

        int calls = h.Calls.Count;
        h.Apply(new Vibration(30000, 0));
        Assert.Equal(calls, h.Calls.Count);
    }

    [Fact]
    public void ASpringRefusedEvenAlone_NeverStopsTheRumble()
    {
        using var h = Wheel();
        h.Settings.AutoCenterStrength = "0";
        h.Apply(new Vibration(30000, 0));
        h.FailCreateOfType = (ushort)SDL_HAPTIC_SPRING;
        h.Settings.AutoCenterStrength = "25";
        h.Apply(new Vibration(30000, 0));
        Assert.True(h.PlayingType(SDL_HAPTIC_LEFTRIGHT));

        // Later frames retry the spring, and only the spring.
        for (int i = 0; i < 3; i++)
        {
            h.Calls.Clear();
            h.Apply(new Vibration(30000, 0));
            output.WriteLine($"frame {i}: {string.Join(',', h.Calls)}");
            Assert.Equal(new[] { "create" }, h.Calls);
            Assert.True(h.PlayingType(SDL_HAPTIC_LEFTRIGHT));
        }
    }

    [Fact]
    public void WhatADeviceHolds_IsLearnedPerHapticHandle()
    {
        using var h = Wheel(room: 1);
        h.Apply(new Vibration());
        h.Apply(new Vibration(30000, 0));
        Assert.False(h.PlayingType(SDL_HAPTIC_LEFTRIGHT));

        // The same row reopened on a device with room for both.
        h.Room = int.MaxValue;
        h.Set("Haptic", new IntPtr(2));
        h.Apply(new Vibration(30000, 0));
        Assert.True(h.PlayingType(SDL_HAPTIC_SPRING));
        Assert.True(h.PlayingType(SDL_HAPTIC_LEFTRIGHT));
    }

    [Fact]
    public void Stop_ReleasesTheSpringAndTheRumble()
    {
        using var h = Wheel();
        h.Apply(new Vibration(30000, 0));
        Assert.Equal(2, h.Allocated.Count);
        h.State.StopDeviceForces(h.Device);
        Assert.Empty(h.Allocated);
        Assert.False(h.Running);
        Assert.False(h.State.IsActive);
    }
}
