using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests;

/// <summary>
/// The Motion Pitch, Yaw and Roll rows (#475) through the real passes: the
/// motion snapshot, Step 3, Step 4 and the motion stage, in poll order.
/// </summary>
[Collection("SettingsManagerStatics")]
public class MotionRowsEngineTests
{
    private const int Center = 32768;

    private sealed class Pad : WebControllerDevice, ISdlInputDevice
    {
        public Pad() : base(Guid.NewGuid().ToString(), "Motion Rows Pad") { }
        public bool HasGyroAux { get; set; }
        public bool HasAccelAux { get; set; }
    }

    private static MappingSource Source(UserDevice d, string descriptor)
        => new() { Kind = "Direct", DeviceGuid = d.InstanceGuidString, Descriptor = descriptor };

    private static MappingRow Row(string target, params MappingSource[] sources)
        => new() { Target = target, LayerMask = "Base", MotionDeadzone = 0, Sources = new List<MappingSource>(sources) };

    // ── The #473 setup ──

    [Fact]
    public void TheSetupFrom473StreamsMotionOnceAStickDrivesMotionYaw()
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: false, accel: false);
        // PalFanTw's Motion Gyro row: four Left Stick halves the row cannot
        // read, and an empty Motion Accelerometer row.
        var gyroRow = Row(MappingSetMigrator.MotionGyroTarget,
            new MappingSource { Kind = "Direct", DeviceGuid = pad.InstanceGuidString, Descriptor = "Axis 1", Invert = true, HalfAxis = true },
            new MappingSource { Kind = "Direct", DeviceGuid = pad.InstanceGuidString, Descriptor = "Axis 1", HalfAxis = true },
            new MappingSource { Kind = "Direct", DeviceGuid = pad.InstanceGuidString, Descriptor = "Axis 0", Invert = true, HalfAxis = true },
            new MappingSource { Kind = "Direct", DeviceGuid = pad.InstanceGuidString, Descriptor = "Axis 0", HalfAxis = true });
        var set = rig.Slot(0, VirtualControllerType.Nintendo, gyroRow, Row(MappingSetMigrator.MotionAccelTarget));

        rig.Poll();
        Assert.False(rig.Manager.MotionSnapshots[0].HasMotion);

        set.Rows.Add(Row(MappingSetMigrator.MotionYawTarget, Source(pad, "Axis 0")));
        pad.InputState.Axis[0] = 65535;   // full right
        rig.Poll();
        rig.Poll();

        var snap = rig.Manager.MotionSnapshots[0];
        Assert.True(snap.HasMotion);
        Assert.Equal(-360f, snap.GyroYaw, 1f);       // a right turn at the default speed
        Assert.Equal(0f, snap.GyroPitch, 0.01f);
        Assert.Equal(0f, snap.GyroRoll, 0.01f);
        // SDL's Switch driver drops an all-zero accelerometer. Turning about
        // the vertical leaves 1 g on +Y.
        Assert.Equal(1f, snap.AccelY, 0.001f);
        Assert.Equal(1f, MathF.Sqrt(snap.AccelX * snap.AccelX + snap.AccelY * snap.AccelY + snap.AccelZ * snap.AccelZ), 0.001f);
        // DSU gets the same frame, so its controller model reports a full gyro.
        Assert.Equal(snap.GyroYaw, rig.Manager.DsuMotionSnapshots[0].GyroYaw);
        Assert.True(rig.Manager.DsuMotionSnapshots[0].HasMotion);
        using (var server = new DsuMotionServer())
        {
            var packet = (byte[])typeof(DsuMotionServer).GetMethod("BuildPadDataPacket",
                BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(server, new object[] { 0, rig.Manager.DsuMotionSnapshots[0], true });
            Assert.Equal(2, packet[22]);                                                     // full gyro
            Assert.Equal(360f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(92)), 1f); // DSU yaw is SDL's negated
        }

        pad.InputState.Axis[0] = Center;
        rig.Poll();
        snap = rig.Manager.MotionSnapshots[0];
        Assert.True(snap.HasMotion, "the controller keeps streaming at rest");
        Assert.Equal(0f, snap.GyroYaw, 0.01f);
    }

    [Fact]
    public void RollRollsAndLeansRightThroughStep3()
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: false, accel: false);
        var roll = Row(MappingSetMigrator.MotionRollTarget, Source(pad, "Axis 0"));
        rig.Slot(0, VirtualControllerType.Nintendo, roll);
        pad.InputState.Axis[0] = 65535;      // right
        rig.Poll();
        rig.Poll();
        Assert.Equal(-360f, rig.Manager.MotionSnapshots[0].GyroRoll, 1f);   // Speed: rolls right

        roll.MotionResponse = MappingRow.MotionResponseAngle;
        roll.MotionAngle = 30;
        InputManager.RequestMotionRowsLevel(0);
        for (int i = 0; i < 60; i++) rig.Poll(sleepMs: 5);
        var snap = rig.Manager.MotionSnapshots[0];
        // Angle: the right side down 30°, gravity toward the right.
        Assert.Equal(-0.5f, snap.AccelX, 0.01f);
        Assert.Equal(MathF.Cos(30f * MathF.PI / 180f), snap.AccelY, 0.01f);
    }

    [Theory]
    [InlineData(VirtualControllerType.PlayStation)]
    [InlineData(VirtualControllerType.Nintendo)]
    public void EachResponseTurnsTheWayItsPrecedentDoes(VirtualControllerType type)
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: false, accel: false);
        var pitch = Row(MappingSetMigrator.MotionPitchTarget, Source(pad, "Axis 1"));
        var set = rig.Slot(0, type, pitch);

        pad.InputState.Axis[1] = 0;      // stick pushed up
        rig.Poll();
        rig.Poll();
        Assert.True(rig.Manager.MotionSnapshots[0].GyroPitch > 300f, "Speed: up raises the nose");

        // Angle: up tips the controller forward, nose down.
        pitch.MotionResponse = MappingRow.MotionResponseAngle;
        pitch.MotionAngle = 30;
        for (int i = 0; i < 40; i++) rig.Poll(sleepMs: 5);
        var snap = rig.Manager.MotionSnapshots[0];
        Assert.True(snap.AccelZ > 0.1f, $"a forward tip reads gravity toward the player, got Z={snap.AccelZ}");
        Assert.True(set.Rows.Count == 1);
    }

    [Fact]
    public void TheRowSettingsReachTheModel()
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: false, accel: false);
        var yaw = Row(MappingSetMigrator.MotionYawTarget, Source(pad, "Axis 0"));
        yaw.MotionSpeed = 90;
        yaw.MotionMinSpeed = 30;
        yaw.MotionDeadzone = 50;
        rig.Slot(0, VirtualControllerType.PlayStation, yaw);
        pad.InputState.Axis[0] = Center + 24575;     // three quarters right
        rig.Poll();
        rig.Poll();
        // Past a 50% deadzone, three quarters is halfway: 30 + (90 - 30) / 2.
        Assert.Equal(-60f, rig.Manager.MotionSnapshots[0].GyroYaw, 1f);
    }

    // ── False positives ──

    [Theory]
    [InlineData(VirtualControllerType.PlayStation)]
    [InlineData(VirtualControllerType.Nintendo)]
    [InlineData(VirtualControllerType.Xbox)]
    [InlineData(VirtualControllerType.Extended)]
    public void ASlotWithoutTheRowsKeepsItsMotionBitForBit(VirtualControllerType type)
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: true, accel: true);
        pad.InputState.Gyro[0] = 0.3f; pad.InputState.Gyro[1] = -0.2f; pad.InputState.Gyro[2] = 0.1f;
        pad.InputState.Accel[0] = 1f; pad.InputState.Accel[1] = 9f; pad.InputState.Accel[2] = -2f;
        rig.Slot(0, type,
            Row(MappingSetMigrator.MotionGyroTarget, Source(pad, MappingSetMigrator.MotionGyroSourceDescriptor)),
            Row(MappingSetMigrator.MotionAccelTarget, Source(pad, MappingSetMigrator.MotionAccelSourceDescriptor)));

        rig.Snapshots();
        var before = rig.Manager.MotionSnapshots[0];
        var beforeDsu = rig.Manager.DsuMotionSnapshots[0];
        rig.Poll();
        var after = rig.Manager.MotionSnapshots[0];
        Assert.Equal(before.GyroPitch, after.GyroPitch);
        Assert.Equal(before.GyroYaw, after.GyroYaw);
        Assert.Equal(before.GyroRoll, after.GyroRoll);
        Assert.Equal(before.AccelX, after.AccelX);
        Assert.Equal(before.AccelY, after.AccelY);
        Assert.Equal(before.AccelZ, after.AccelZ);
        Assert.Equal(before.HasMotion, after.HasMotion);
        Assert.Equal(beforeDsu.GyroYaw, rig.Manager.DsuMotionSnapshots[0].GyroYaw);
    }

    [Fact]
    public void AnXboxSlotIgnoresTheRows()
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: false, accel: false);
        rig.Slot(0, VirtualControllerType.Xbox, Row(MappingSetMigrator.MotionYawTarget, Source(pad, "Axis 0")));
        pad.InputState.Axis[0] = 65535;
        rig.Poll();
        rig.Poll();
        Assert.False(rig.Manager.MotionSnapshots[0].HasMotion);
        Assert.Equal(0f, rig.Manager.CombinedMotionRows[0].Yaw);
    }

    [Fact]
    public void TheRowsNeverReachTheGamepad()
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: false, accel: false);
        rig.Slot(0, VirtualControllerType.PlayStation,
            Row(MappingSetMigrator.MotionYawTarget, Source(pad, "Axis 0")),
            Row(MappingSetMigrator.MotionPitchTarget, Source(pad, "Button 0")));
        pad.InputState.Axis[0] = 65535;
        pad.InputState.Buttons[0] = true;
        rig.Poll();
        var gp = rig.Manager.CombinedOutputStates[0];
        Assert.Equal(0, gp.ThumbLX);
        Assert.Equal(0, gp.ThumbRX);
        Assert.Equal(0, gp.Buttons);
    }

    [Fact]
    public void EmptyOrModifierOnlyRowsLeaveASensorlessSlotSilent()
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: false, accel: false);
        // The grid saves every row, so a PlayStation slot carries the three
        // rows empty from its first save.
        var set = rig.Slot(0, VirtualControllerType.PlayStation,
            Row(MappingSetMigrator.MotionPitchTarget),
            Row(MappingSetMigrator.MotionYawTarget),
            Row(MappingSetMigrator.MotionRollTarget));
        rig.Poll();
        Assert.False(rig.Manager.MotionSnapshots[0].HasMotion);
        Assert.False(rig.Manager.DsuMotionSnapshots[0].HasMotion);

        // A source added in place reaches the gate at its next 250 ms scan.
        set.Rows[1].Sources.Add(new MappingSource
        {
            Kind = "InvertOnHold", DeviceGuid = pad.InstanceGuidString, ParamModifier = "Button 0",
        });
        rig.Poll(sleepMs: 260);
        Assert.False(rig.Manager.MotionSnapshots[0].HasMotion);

        // Positive control: the same row with a stick on it streams.
        set.Rows[1].Sources.Add(Source(pad, "Axis 0"));
        rig.Poll(sleepMs: 260);
        Assert.True(rig.Manager.MotionSnapshots[0].HasMotion);
    }

    [Fact]
    public void AnEmptiedMotionGyroRowKeepsTheRealGyroOut()
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: true, accel: true);
        pad.InputState.Gyro[1] = 0.5f;
        pad.InputState.Accel[1] = 9.80665f;
        // Emptying the Motion Gyro row is how a user switches the real gyro off.
        rig.Slot(0, VirtualControllerType.PlayStation,
            Row(MappingSetMigrator.MotionGyroTarget),
            Row(MappingSetMigrator.MotionAccelTarget, Source(pad, MappingSetMigrator.MotionAccelSourceDescriptor)),
            Row(MappingSetMigrator.MotionYawTarget, Source(pad, "Axis 0")));
        pad.InputState.Axis[0] = 65535;
        rig.Poll();
        rig.Poll();
        Assert.Equal(-360f, rig.Manager.MotionSnapshots[0].GyroYaw, 1f);
    }

    [Fact]
    public void ASpeedTurnLeavesARealAccelerometerAloneAndALeanTurnsIt()
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: true, accel: true);
        pad.InputState.Accel[1] = 9.80665f;
        var pitch = Row(MappingSetMigrator.MotionPitchTarget, Source(pad, "Axis 1"));
        rig.Slot(0, VirtualControllerType.PlayStation,
            Row(MappingSetMigrator.MotionGyroTarget, Source(pad, MappingSetMigrator.MotionGyroSourceDescriptor)),
            Row(MappingSetMigrator.MotionAccelTarget, Source(pad, MappingSetMigrator.MotionAccelSourceDescriptor)),
            pitch);
        pad.InputState.Axis[1] = 0;      // nose up at 360°/s
        for (int i = 0; i < 10; i++) rig.Poll(sleepMs: 5);
        var snap = rig.Manager.MotionSnapshots[0];
        Assert.True(snap.GyroPitch > 300f);
        // A Speed turn adds rate. The attitude stays the real controller's.
        Assert.Equal(1f, snap.AccelY, 0.001f);
        Assert.Equal(0f, snap.AccelZ, 0.001f);

        pitch.MotionResponse = MappingRow.MotionResponseAngle;
        pitch.MotionAngle = 30;
        for (int i = 0; i < 40; i++) rig.Poll(sleepMs: 5);
        snap = rig.Manager.MotionSnapshots[0];
        Assert.Equal(MathF.Sin(30f * MathF.PI / 180f), snap.AccelZ, 0.01f);
        Assert.Equal(MathF.Cos(30f * MathF.PI / 180f), snap.AccelY, 0.01f);
    }

    // ── Devices coming and going ──

    [Fact]
    public void WhenTheOnlyControllerDropsTheSlotGoesQuietAndKeepsItsPose()
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: false, accel: false);
        rig.Slot(0, VirtualControllerType.PlayStation, Row(MappingSetMigrator.MotionPitchTarget, Source(pad, "Axis 1")));
        pad.InputState.Axis[1] = 0;
        for (int i = 0; i < 20; i++) rig.Poll(sleepMs: 5);
        pad.InputState.Axis[1] = Center;
        rig.Poll();
        float held = rig.Manager.MotionSnapshots[0].AccelY;
        Assert.True(held < 0.99f, "the nose was raised");

        pad.IsOnline = false;
        rig.Poll();
        Assert.False(rig.Manager.MotionSnapshots[0].HasMotion);

        // It comes back with the stick already pushed. The gap is no turn,
        // and the turn starts on the next frame.
        pad.InputState.Axis[1] = 0;
        pad.IsOnline = true;
        Thread.Sleep(100);
        rig.Poll();
        var snap = rig.Manager.MotionSnapshots[0];
        Assert.True(snap.HasMotion);
        Assert.Equal(held, snap.AccelY, 0.001f);     // the pose held
        Assert.Equal(0f, snap.GyroPitch, 0.01f);     // and the gap was no turn
        rig.Poll();
        Assert.True(rig.Manager.MotionSnapshots[0].GyroPitch > 300f);
    }

    [Fact]
    public void RemovingTheRowsStartsThePoseOver()
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: false, accel: false);
        var set = rig.Slot(0, VirtualControllerType.PlayStation, Row(MappingSetMigrator.MotionPitchTarget, Source(pad, "Axis 1")));
        pad.InputState.Axis[1] = 0;
        for (int i = 0; i < 20; i++) rig.Poll(sleepMs: 5);
        pad.InputState.Axis[1] = Center;
        rig.Poll();
        Assert.True(rig.Manager.MotionSnapshots[0].AccelY < 0.99f, "the nose was raised");

        var row = set.Rows[0];
        set.Rows.Clear();
        rig.Poll();
        Assert.False(rig.Manager.MotionSnapshots[0].HasMotion);
        set.Rows.Add(row);
        rig.Poll();
        Assert.Equal(1f, rig.Manager.MotionSnapshots[0].AccelY, 0.0001f);
    }

    [Fact]
    public void ValuesFromAnEarlierPassDoNotTurnTheSlot()
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: false, accel: false);
        rig.Slot(0, VirtualControllerType.PlayStation, Row(MappingSetMigrator.MotionYawTarget, Source(pad, "Axis 0")));
        pad.InputState.Axis[0] = 65535;
        rig.Poll();
        rig.Poll();
        Assert.Equal(-360f, rig.Manager.MotionSnapshots[0].GyroYaw, 1f);
        rig.PollWithoutStep4();          // a pass in which Step 4 skipped the slot
        Assert.Equal(0f, rig.Manager.MotionSnapshots[0].GyroYaw, 0.01f);
    }

    [Fact]
    public void AControllerThatDropsMidTurnStopsTurningTheSlot()
    {
        using var rig = new Rig();
        var keeper = rig.AddDevice(0, gyro: false, accel: false);
        var turner = rig.AddDevice(0, gyro: false, accel: false);
        rig.Slot(0, VirtualControllerType.PlayStation, Row(MappingSetMigrator.MotionYawTarget, Source(turner, "Axis 0")));
        turner.InputState.Axis[0] = 65535;
        rig.Poll();
        rig.Poll();
        Assert.Equal(-360f, rig.Manager.MotionSnapshots[0].GyroYaw, 1f);

        turner.IsOnline = false;         // asleep with the stick held
        rig.Poll();
        rig.Poll();
        Assert.Equal(0f, rig.Manager.CombinedMotionRows[0].Yaw);
        Assert.Equal(0f, rig.Manager.MotionSnapshots[0].GyroYaw, 0.01f);
        Assert.True(rig.Manager.MotionSnapshots[0].HasMotion, "the slot still streams through the other controller");
        Assert.True(keeper.IsOnline);
    }

    // ── With a real controller's motion ──

    [Fact]
    public void RealAndSimulatedRatesAddAndTheRealGravityStays()
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: true, accel: true);
        pad.InputState.Gyro[1] = 0.5f;                          // 28.6°/s of real yaw
        pad.InputState.Accel[1] = 9.80665f;
        rig.Slot(0, VirtualControllerType.PlayStation,
            Row(MappingSetMigrator.MotionGyroTarget, Source(pad, MappingSetMigrator.MotionGyroSourceDescriptor)),
            Row(MappingSetMigrator.MotionAccelTarget, Source(pad, MappingSetMigrator.MotionAccelSourceDescriptor)),
            Row(MappingSetMigrator.MotionYawTarget, Source(pad, "Axis 0")));
        pad.InputState.Axis[0] = 65535;
        rig.Poll();
        rig.Poll();
        var snap = rig.Manager.MotionSnapshots[0];
        Assert.Equal(0.5f * 180f / MathF.PI - 360f, snap.GyroYaw, 1f);
        Assert.Equal(1f, snap.AccelY, 0.001f);
        Assert.Equal(0f, snap.AccelX, 0.001f);
    }

    // ── Recenter and resets ──

    [Fact]
    public void GyroRecenterLevelsTheTurnAndAResetStartsOver()
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: false, accel: false);
        rig.Slot(0, VirtualControllerType.PlayStation, Row(MappingSetMigrator.MotionPitchTarget, Source(pad, "Axis 1")));
        pad.InputState.Axis[1] = 0;
        for (int i = 0; i < 20; i++) rig.Poll(sleepMs: 5);
        pad.InputState.Axis[1] = Center;
        rig.Poll();
        Assert.True(rig.Manager.MotionSnapshots[0].AccelY < 0.99f, "the nose was raised");

        InputManager.RequestMotionRowsLevel(0);
        rig.Poll();
        var snap = rig.Manager.MotionSnapshots[0];
        Assert.Equal(1f, snap.AccelY, 0.0001f);
        Assert.Equal(0f, snap.GyroPitch, 0.01f);   // the jump is not reported as rotation

        pad.InputState.Axis[1] = 0;
        for (int i = 0; i < 20; i++) rig.Poll(sleepMs: 5);
        InputManager.ClearSourceKindRuntime();     // the profile-switch lane
        pad.InputState.Axis[1] = Center;
        rig.Poll();
        Assert.Equal(1f, rig.Manager.MotionSnapshots[0].AccelY, 0.0001f);
    }

    // ── Sources that rest at zero ──

    [Theory]
    [InlineData("Axis 5", false)]
    [InlineData("Axis 5", true)]
    [InlineData("Gamepad RightTrigger", false)]
    public void ATriggerTurnsOneWayAndRestsStill(string descriptor, bool invert)
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: false, accel: false);
        pad.InputState.Axis[5] = 0;                  // released
        var src = Source(pad, descriptor);
        src.Invert = invert;
        rig.Slot(0, VirtualControllerType.PlayStation, Row(MappingSetMigrator.MotionYawTarget, src));
        rig.Poll();
        rig.Poll();
        Assert.Equal(0f, rig.Manager.CombinedMotionRows[0].Yaw);
        Assert.Equal(0f, rig.Manager.MotionSnapshots[0].GyroYaw, 0.01f);

        pad.InputState.Axis[5] = 65535;              // fully pulled
        rig.Poll();
        rig.Poll();
        Assert.Equal(invert ? -1f : 1f, rig.Manager.CombinedMotionRows[0].Yaw, 0.001f);
        Assert.Equal(invert ? 360f : -360f, rig.Manager.MotionSnapshots[0].GyroYaw, 1f);
    }

    [Fact]
    public void AJoysticksAxisTwoStaysCentered()
    {
        // Axis 2 is a trigger only on a gamepad's SDL layout. A joystick's
        // Axis 2 is a centered axis, read both ways.
        using var rig = new Rig();
        var stick = rig.AddDevice(0, gyro: false, accel: false, cap: InputDeviceType.Joystick);
        rig.Slot(0, VirtualControllerType.PlayStation, Row(MappingSetMigrator.MotionYawTarget, Source(stick, "Axis 2")));
        rig.Poll();
        Assert.Equal(0f, rig.Manager.CombinedMotionRows[0].Yaw, 0.001f);
        stick.InputState.Axis[2] = 0;
        rig.Poll();
        Assert.Equal(-1f, rig.Manager.CombinedMotionRows[0].Yaw, 0.001f);
    }

    [Fact]
    public void AToggledTriggerLatchesOnAPullAndNotAtRest()
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: false, accel: false);
        pad.InputState.Axis[5] = 0;
        var src = Source(pad, "Axis 5");
        src.Kind = "Toggle";
        rig.Slot(0, VirtualControllerType.PlayStation, Row(MappingSetMigrator.MotionYawTarget, src));
        rig.Poll();
        rig.Poll();
        Assert.Equal(0f, rig.Manager.CombinedMotionRows[0].Yaw);

        pad.InputState.Axis[5] = 65535;
        rig.Poll();
        Assert.Equal(1f, rig.Manager.CombinedMotionRows[0].Yaw, 0.001f);
        pad.InputState.Axis[5] = 0;
        rig.Poll();
        Assert.Equal(1f, rig.Manager.CombinedMotionRows[0].Yaw, 0.001f);   // latched
        pad.InputState.Axis[5] = 65535;
        rig.Poll();
        pad.InputState.Axis[5] = 0;
        rig.Poll();
        Assert.Equal(0f, rig.Manager.CombinedMotionRows[0].Yaw);
    }

    [Fact]
    public void AStickRowStillReadsATriggerCentered()
    {
        // The one-way read belongs to the motion rows. A stick row keeps the
        // full-axis read it has always had.
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: false, accel: false);
        pad.InputState.Axis[5] = 0;
        rig.Slot(0, VirtualControllerType.PlayStation, Row("LeftThumbAxisX", Source(pad, "Axis 5")));
        rig.Poll();
        Assert.True(rig.Manager.CombinedOutputStates[0].ThumbLX < -32000,
            $"the stick row read {rig.Manager.CombinedOutputStates[0].ThumbLX}");
    }

    // ── Valve ──

    [Theory]
    [InlineData("steam-deck-composite", true)]
    [InlineData("steam-controller", true)]
    [InlineData("steam-controller-2", true)]
    [InlineData(HMaestroProfileCatalog.CustomProfileId, false)]
    public void AnExtendedSlotCarriesTheRowsOnAValveProfile(string profile, bool carries)
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: false, accel: false);
        rig.Slot(0, VirtualControllerType.Extended, Row(MappingSetMigrator.MotionYawTarget, Source(pad, "Axis 0")));
        rig.Manager.SlotRawHidSurface[0] = true;
        rig.Manager.SlotProfileIds[0] = profile;
        pad.InputState.Axis[0] = 65535;
        rig.Poll();
        rig.Poll();
        Assert.Equal(carries, rig.Manager.MotionSnapshots[0].HasMotion);
        if (carries) Assert.Equal(-360f, rig.Manager.MotionSnapshots[0].GyroYaw, 1f);
        else Assert.Equal(0f, rig.Manager.CombinedMotionRows[0].Yaw);
    }

    // ── Two controllers on a slot ──

    [Fact]
    public void TwoControllersCombineByTheLargerPush()
    {
        using var rig = new Rig();
        var a = rig.AddDevice(0, gyro: false, accel: false);
        var b = rig.AddDevice(0, gyro: false, accel: false);
        rig.Slot(0, VirtualControllerType.PlayStation,
            Row(MappingSetMigrator.MotionYawTarget, Source(a, "Axis 0"), Source(b, "Axis 0")));
        a.InputState.Axis[0] = Center + 16384;   // half right
        b.InputState.Axis[0] = 0;                // full left
        rig.Poll();
        rig.Poll();
        Assert.Equal(-1f, rig.Manager.CombinedMotionRows[0].Yaw, 0.01f);
        Assert.Equal(360f, rig.Manager.MotionSnapshots[0].GyroYaw, 1f);   // a left turn
    }

    // ── Recenter from a macro ──

    [Fact]
    public void AGyroRecenterMacroLevelsTheTurnOnItsSlot()
    {
        const int slot = 13;
        var hook = InputManager.GyroRecenterApply;
        using var rig = new Rig();
        try
        {
            InputManager.GyroRecenterApply = null;
            var pad = rig.AddDevice(slot, gyro: false, accel: false);
            rig.Slot(slot, VirtualControllerType.PlayStation, Row(MappingSetMigrator.MotionPitchTarget, Source(pad, "Axis 1")));
            pad.InputState.Axis[1] = 0;
            for (int i = 0; i < 20; i++) rig.Poll(sleepMs: 5);
            pad.InputState.Axis[1] = Center;
            rig.Poll();
            Assert.True(rig.Manager.MotionSnapshots[slot].AccelY < 0.99f, "the nose was raised");

            var macro = new MacroItem
            {
                Name = "Recenter", IsEnabled = true, PadIndex = slot, TriggerButtons = Gamepad.A,
                TriggerMode = MacroTriggerMode.OnPress, RepeatMode = MacroRepeatMode.Once,
            };
            macro.Actions.Add(new MacroAction { Type = MacroActionType.GyroRecenter });
            var gp = new Gamepad { Buttons = Gamepad.A };
            rig.Manager.EvaluateSlotMacros(ref gp, new[] { macro });
            rig.Poll();
            Assert.Equal(1f, rig.Manager.MotionSnapshots[slot].AccelY, 0.0001f);
        }
        finally { InputManager.GyroRecenterApply = hook; }
    }

    // ── The lanes that start a pose over ──

    private static int[] Requests() => (int[])typeof(InputManager)
        .GetField("s_motionRowsRequests", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

    [Fact]
    public void EachLaneThatReplacesASlotsRowsStartsThePoseOver()
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: false, accel: false);
        rig.Slot(0, VirtualControllerType.PlayStation, Row(MappingSetMigrator.MotionYawTarget, Source(pad, "Axis 0")));
        rig.Slot(1, VirtualControllerType.PlayStation);
        const int reset = 2;
        var requests = Requests();

        Array.Clear(requests);
        InputService.ApplySlotMappingSetFromRows(1, new List<MappingRow>());       // Paste
        Assert.Equal(reset, requests[1] & reset);
        Assert.Equal(0, requests[0]);

        Array.Clear(requests);
        InputService.ReplaceSlotMappingSet(1, 0);                                   // Copy From
        Assert.Equal(reset, requests[1] & reset);
        Assert.Equal(0, requests[0]);

        Array.Clear(requests);
        InputManager.ResetSourceKindRuntimeForSlot(3);                              // a deleted slot
        Assert.Equal(reset, requests[3] & reset);
        Assert.Equal(0, requests[0]);

        Array.Clear(requests);
        InputManager.ClearSourceKindRuntime();                                      // a profile switch
        Assert.All(requests, r => Assert.Equal(reset, r & reset));
        Array.Clear(requests);
    }

    [Fact]
    public void ANeutralEdgeStopsTheTurnAndRestartsTheClock()
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: false, accel: false);
        rig.Slot(0, VirtualControllerType.PlayStation, Row(MappingSetMigrator.MotionYawTarget, Source(pad, "Axis 0")));
        pad.InputState.Axis[0] = 65535;
        rig.Poll();
        rig.Poll();
        Assert.Equal(1f, rig.Manager.CombinedMotionRows[0].Yaw, 0.01f);

        typeof(InputManager).GetMethod("NeutralizeCombinedOutputs", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(rig.Manager, null);
        Assert.Equal(0f, rig.Manager.CombinedMotionRows[0].Yaw);
        Assert.False(rig.Manager.MotionSnapshots[0].HasMotion);

        // Back from the pause, the first poll reports no turn through the gap.
        Thread.Sleep(120);
        rig.Poll();
        Assert.Equal(0f, rig.Manager.MotionSnapshots[0].GyroYaw);
        rig.Poll();
        Assert.Equal(-360f, rig.Manager.MotionSnapshots[0].GyroYaw, 1f);
    }

    // ── Rows like any other row ──

    [Fact]
    public void ACustomFormulaReadsAnOppositeDirectionPairAsOneValue()
    {
        using var rig = new Rig();
        var pad = rig.AddDevice(0, gyro: false, accel: false);
        // Button 1 turns right and Button 2, its Opposite Direction, turns
        // left. On a stick row the pair is one formula value, a.
        var yaw = Row(MappingSetMigrator.MotionYawTarget,
            Source(pad, "Button 1"),
            new MappingSource { Kind = "Direct", DeviceGuid = pad.InstanceGuidString, Descriptor = "Button 2", Invert = true });
        yaw.CombineMode = "Custom";
        yaw.CombineExpression = "a";
        rig.Slot(0, VirtualControllerType.Nintendo, yaw);

        pad.InputState.Buttons[2] = true;
        rig.Poll();
        rig.Poll();
        Assert.Equal(-1f, rig.Manager.CombinedMotionRows[0].Yaw, 0.01f);
        Assert.Equal(360f, rig.Manager.MotionSnapshots[0].GyroYaw, 1f);   // a left turn
    }

    [Fact]
    public void AMotionRowKeepsItsToggleLayerFromCancelingMidTurn()
    {
        InputManager.ClearAllShiftRuntime();
        try
        {
            using var rig = new Rig();
            var pad = rig.AddDevice(0, gyro: false, accel: false);
            var yaw = Row(MappingSetMigrator.MotionYawTarget, Source(pad, "Axis 0"));
            yaw.LayerMask = "View";
            var set = rig.Slot(0, VirtualControllerType.Nintendo, yaw);
            // A Toggle layer that switches itself off after 150 ms in which
            // none of its rows produce output (#206).
            set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "", Descriptor = "Button 28", Mode = "Toggle",
                LayerMask = "View", LayerName = "View", Kind = "Button",
                DelayMs = 0, AutoCancelMs = 150,
            });

            pad.InputState.Buttons[28] = true;
            rig.Poll();
            pad.InputState.Buttons[28] = false;
            pad.InputState.Axis[0] = 65535;            // turning right on the layer's row
            for (int i = 0; i < 25; i++) rig.Poll(sleepMs: 20);
            Assert.Equal(-360f, rig.Manager.MotionSnapshots[0].GyroYaw, 1f);

            // At rest the layer has no output, cancels, and takes the row
            // with it: the same push then turns nothing.
            pad.InputState.Axis[0] = Center;
            for (int i = 0; i < 15; i++) rig.Poll(sleepMs: 20);
            pad.InputState.Axis[0] = 65535;
            rig.Poll();
            rig.Poll();
            Assert.Equal(0f, rig.Manager.MotionSnapshots[0].GyroYaw, 0.01f);
        }
        finally { InputManager.ClearAllShiftRuntime(); }
    }

    // ── The rig ──

    private sealed class Rig : IDisposable
    {
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly bool[] _created = SettingsManager.SlotCreated;
        private readonly bool[] _enabled = SettingsManager.SlotEnabled;
        private readonly Func<string, int, SourceCoercion.GyroTuning> _tuning = SourceCoercion.GyroTuningProvider;
        private readonly Func<string, int, (float, float, float)> _bias = SourceCoercion.GyroBiasProvider;
        private readonly Func<string, string, bool> _restsAtZero = SourceCoercion.SourceRestsAtZeroProvider;
        private readonly List<Pad> _wrappers = new();
        private readonly Action _snapshots, _step3, _step4, _stage;
        public InputManager Manager { get; } = new();

        public Rig()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
            SettingsManager.SlotEnabled = new bool[InputManager.MaxPads];
            SourceCoercion.GyroTuningProvider = (_, _) => new SourceCoercion.GyroTuning
            {
                Grip = "Pointing", ApplyToPassthrough = false,
            };
            SourceCoercion.GyroBiasProvider = (_, _) => (0f, 0f, 0f);
            SourceCoercion.SourceRestsAtZeroProvider = InputManager.SourceRestsAtZero;
            Action Bind(string name) => typeof(InputManager).GetMethod(name,
                BindingFlags.Instance | BindingFlags.NonPublic).CreateDelegate<Action>(Manager);
            _snapshots = Bind("UpdateMotionSnapshots");
            _step3 = Bind("UpdateOutputStates");
            _step4 = Bind("CombineOutputStates");
            _stage = Bind("ApplyMotionRows");
            InputManager.RequestMotionRowsReset(-1);
        }

        public UserDevice AddDevice(int slot, bool gyro, bool accel, int cap = InputDeviceType.Gamepad)
        {
            var wrapper = new Pad { HasGyro = gyro, HasAccel = accel };
            _wrappers.Add(wrapper);
            var state = new CustomInputState();
            for (int i = 0; i < 6; i++) state.Axis[i] = Center;
            var device = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), Device = wrapper, InputState = state,
                IsOnline = true, HasGyro = gyro, HasAccel = accel,
                CapType = cap,
            };
            SettingsManager.UserDevices.Items.Add(device);
            var setting = new UserSetting { InstanceGuid = device.InstanceGuid, MapTo = slot };
            setting.SetPadSetting(new PadSetting());
            SettingsManager.UserSettings.Items.Add(setting);
            return device;
        }

        public MappingSet Slot(int slot, VirtualControllerType type, params MappingRow[] rows)
        {
            SettingsManager.SlotCreated[slot] = true;
            SettingsManager.SlotEnabled[slot] = true;
            Manager.SlotControllerTypes[slot] = type;
            var set = new MappingSet();
            set.Rows.AddRange(rows);
            SettingsManager.SlotMappingSets[slot] = set;
            return set;
        }

        /// <summary>The motion snapshot pass alone.</summary>
        public void Snapshots() => _snapshots();

        /// <summary>One poll in the engine's order.</summary>
        public void Poll(int sleepMs = 2)
        {
            Thread.Sleep(sleepMs);
            _snapshots();
            _step3();
            _step4();
            _stage();
        }

        /// <summary>One poll whose Step 4 never reached the slot.</summary>
        public void PollWithoutStep4(int sleepMs = 2)
        {
            Thread.Sleep(sleepMs);
            _snapshots();
            _step3();
            _stage();
        }

        public void Dispose()
        {
            try
            {
                Manager.Dispose();
                foreach (var w in _wrappers) w.Dispose();
            }
            finally
            {
                InputManager.RequestMotionRowsReset(-1);
                SettingsManager.UserSettings = _settings;
                SettingsManager.UserDevices = _devices;
                SettingsManager.SlotMappingSets = _sets;
                SettingsManager.SlotCreated = _created;
                SettingsManager.SlotEnabled = _enabled;
                SourceCoercion.GyroTuningProvider = _tuning;
                SourceCoercion.GyroBiasProvider = _bias;
                SourceCoercion.SourceRestsAtZeroProvider = _restsAtZero;
            }
        }
    }
}
