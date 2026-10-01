using System;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests;

/// <summary>
/// Player and World space turn the same way as Local space. The gravity
/// PadForge keeps is the accelerometer's reading, which points up, while
/// GamepadMotionHelpers' projections take gravity pointing down: "When the
/// controller is still on a flat surface it'll be approximately (0, -1, 0)"
/// (README.md, GetGravity), and "Make sure you use this GamepadMotionHelpers'
/// coordinate space, units, and gravity" (README.md, CalculatePlayerSpaceGyro).
/// Fed the reading as it was, a flat pad turning left read +0.115 in Local
/// space and -0.115 in Player and World space.
/// </summary>
[Collection("SettingsManagerStatics")]
public class GyroSpaceSignTests
{
    private const float G = 9.81f;
    private const float Deg = MathF.PI / 180f;

    private static float Read(string space, (float x, float y, float z) gravity, float pitch, float yaw, float roll,
        string descriptor = "Gyro Yaw")
    {
        var oldTuning = SourceCoercion.GyroTuningProvider;
        var oldGravity = SourceCoercion.GravityProvider;
        var oldBias = SourceCoercion.GyroBiasProvider;
        var oldEngage = SourceCoercion.AimEngageStateProvider;
        try
        {
            SourceCoercion.GyroBiasProvider = null;
            SourceCoercion.AimEngageStateProvider = null;
            SourceCoercion.GravityProvider = _ => gravity;
            SourceCoercion.GyroTuningProvider = (_, _) => new SourceCoercion.GyroTuning
            {
                SensH = 1, SensV = 1, OutputCurve = "Linear", Space = space,
                PlayerYawRelax = 1.41f, WorldSideReduction = 0.125f, Grip = "Pointing",
            };
            var state = new CustomInputState();
            state.Gyro[0] = pitch; state.Gyro[1] = yaw; state.Gyro[2] = roll;
            return SourceCoercion.EvaluateForBipolarAxisTarget(state,
                new MappingSource { Descriptor = descriptor, DeviceGuid = "space-sign" }, 0,
                evaluatedDeviceGuid: "space-sign");
        }
        finally
        {
            SourceCoercion.GyroTuningProvider = oldTuning;
            SourceCoercion.GravityProvider = oldGravity;
            SourceCoercion.GyroBiasProvider = oldBias;
            SourceCoercion.AimEngageStateProvider = oldEngage;
        }
    }

    /// <summary>A flat pad turning left (positive yaw, counter-clockwise
    /// seen from above) reads the same sign in all three spaces.</summary>
    [Theory]
    [InlineData("Player")]
    [InlineData("World")]
    public void AFlatPadTurningLeftReadsLikeLocalSpace(string space)
    {
        var flat = (0f, G, 0f);
        float local = Read("Local", flat, 0, 1, 0);
        float projected = Read(space, flat, 0, 1, 0);
        Assert.True(local > 0);
        Assert.Equal(local, projected, 4);
    }

    /// <summary>Tilted back 30°, a turn about the vertical reaches the pad
    /// as yaw and roll together. Player and World space read it as a turn
    /// the same way round as Local space reads a flat turn.</summary>
    [Theory]
    [InlineData("Player")]
    [InlineData("World")]
    public void ATiltedPadTurningAboutTheVerticalReadsLeft(string space)
    {
        float c = MathF.Cos(30 * Deg), s = MathF.Sin(30 * Deg);
        // Gravity reading after the far edge rises 30°, and the body-frame
        // rate of a turn about the world vertical: the up direction times
        // the rate.
        var up = (0f, G * c, -G * s);
        float projected = Read(space, up, 0, c, -s);
        Assert.True(projected > 0);
    }

    /// <summary>Pitch does not depend on which way gravity points, so the
    /// correction leaves it alone.</summary>
    [Fact]
    public void WorldPitchIsUnchanged()
    {
        var flat = (0f, G, 0f);
        float local = Read("Local", flat, 1, 0, 0, "Gyro Pitch");
        float world = Read("World", flat, 1, 0, 0, "Gyro Pitch");
        Assert.Equal(local, world, 4);
    }

    /// <summary>With no gravity reading the provider falls back to
    /// (0, 0, -1), and Player space then reads roll, banked the way Local
    /// space's Horizontal blend reads it: tilting the top left turns right.</summary>
    [Fact]
    public void TheNoGravityFallbackReadsRollLikeTheHorizontalBlend()
    {
        var none = (0f, 0f, -1f);
        float player = Read("Player", none, 0, 0, 1);
        float local = Read("Local", none, 0, 0, 1, "Gyro Horizontal");
        Assert.True(local < 0);
        Assert.Equal(Math.Sign(local), Math.Sign(player));
    }
}
