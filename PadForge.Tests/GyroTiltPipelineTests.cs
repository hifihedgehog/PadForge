using System;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests;

[Collection("SettingsManagerStatics")]
public class GyroTiltPipelineTests
{
    private static readonly Vector3 Rest = new(0, 9.81f, 0);
    private static Vector3 Roll(float degrees)
    {
        float angle = degrees * MathF.PI / 180f;
        return new Vector3(-MathF.Sin(angle), MathF.Cos(angle), 0) * 9.81f;
    }

    [Fact]
    public void TiltTracksAtPollCadenceWhileBothLeanFamiliesKeepTheirOriginalProvider()
    {
        using var rig = new Rig();
        var device = rig.Add(0);
        rig.Tick(device, Vector3.Zero, Rest, 0);
        Assert.Equal(0, rig.Read(device));
        Assert.Equal(0, rig.Read(device, descriptor: SourceCoercion.GyroLeanXDescriptor));
        var legacy = new SourceKindRuntime();
        var leanSource = new MappingSource { Descriptor = SourceCoercion.MotionLeanDescriptor };
        Assert.Equal(0, legacy.TickMotionLean(0, "LeftThumbAxisX", 0, leanSource, device.InputState, device.InstanceGuidString));

        for (int i = 1; i <= 100; i++)
            rig.Tick(device, new Vector3(0, 0, -MathF.PI / 2), Roll(i * .36f), i * .004);
        Assert.InRange(rig.Read(device), .999f, 1f);
        Assert.Equal(0, rig.Read(device, descriptor: SourceCoercion.GyroLeanXDescriptor));
        Assert.Equal(0, legacy.TickMotionLean(0, "LeftThumbAxisX", 0, leanSource, device.InputState, device.InstanceGuidString));
        // Re-reading any number of mapping rows cannot advance orientation.
        var before = rig.Manager.ReadGyroTiltGravity(device.InstanceGuidString, 0);
        for (int i = 0; i < 100; i++) Assert.InRange(rig.Read(device), .999f, 1f);
        Assert.Equal(before, rig.Manager.ReadGyroTiltGravity(device.InstanceGuidString, 0));
    }

    [Fact]
    public void PollingStepPublishesThePoseWithoutChangingRawMotion()
    {
        using var rig = new Rig();
        var device = rig.Add(0);
        var wrapper = (WebControllerDevice)device.Device;
        wrapper.UpdateMotion(.1f, .2f, .3f, 0, 9.81f, 0);
        var step = typeof(InputManager).GetMethod("UpdateInputStates", BindingFlags.NonPublic | BindingFlags.Instance);
        step.Invoke(rig.Manager, null);
        Assert.Equal(1, device.InputStateSeq);
        var sample = rig.Manager.ReadGyroTiltGravity(device.InstanceGuidString, 0);
        Assert.True(sample.HasValue);
        Assert.InRange(sample.Value.Y, 9.80f, 9.82f);
        Assert.Equal(new[] { .1f, .2f, .3f }, device.InputState.Gyro);
        Assert.Equal(new[] { 0f, 9.81f, 0f }, device.InputState.Accel);
    }

    [Fact]
    public void AnyDeviceTiltCombinesWithThePhysicalStickThroughTheAppEvaluator()
    {
        using var rig = new Rig();
        var device = rig.Add(0);
        device.InputState.Axis[0] = 32767;
        var set = new MappingSet();
        var row = new MappingRow { Target = "LeftThumbAxisX", LayerMask = "Base", CombineMode = "MaxAbs" };
        row.Sources.Add(new MappingSource { Descriptor = "Axis 0", DeviceGuid = device.InstanceGuidString });
        row.Sources.Add(new MappingSource { Descriptor = SourceCoercion.GyroTiltXDescriptor, ParamTiltRangeDeg = 25 });
        set.Rows.Add(row);
        var begin = typeof(InputManager).GetMethod("BeginFrameMultiSourceTracking", BindingFlags.NonPublic | BindingFlags.Static);
        short Read()
        {
            begin.Invoke(null, null);
            Assert.True(InputManager.TryEvaluateMappingSetBipolarAxis(device.InputState, set,
                device.InstanceGuidString, 0, "LeftThumbAxisX", out short value));
            return value;
        }
        InputManager.ClearAllShiftRuntime();
        rig.Tick(device, Vector3.Zero, Rest, 0);
        Assert.InRange((int)Read(), -1, 1);
        for (int i = 1; i <= 100; i++)
            rig.Tick(device, new Vector3(0, 0, -MathF.PI / 2), Roll(i * .36f), i * .004);
        Assert.InRange((int)Read(), 32760, 32767);
    }

    [Fact]
    public void ButtonAndTriggerTargetsReadTheSameResponsiveTilt()
    {
        using var rig = new Rig();
        var device = rig.Add(0);
        var source = new MappingSource { Descriptor = SourceCoercion.GyroTiltXDescriptor,
            DeviceGuid = device.InstanceGuidString, ParamTiltRangeDeg = 25, HalfAxis = true };
        rig.Tick(device, Vector3.Zero, Rest, 0);
        Assert.False(SourceCoercion.EvaluateForButtonTarget(device.InputState, source, 50, 0, device.InstanceGuidString));
        Assert.Equal(0, SourceCoercion.EvaluateForTriggerTarget(device.InputState, source, 0, device.InstanceGuidString));
        for (int i = 1; i <= 100; i++)
            rig.Tick(device, new Vector3(0, 0, -MathF.PI / 2), Roll(i * .36f), i * .004);
        Assert.True(SourceCoercion.EvaluateForButtonTarget(device.InputState, source, 50, 0, device.InstanceGuidString));
        Assert.InRange(SourceCoercion.EvaluateForTriggerTarget(device.InputState, source, 0, device.InstanceGuidString), .999f, 1f);
    }

    [Fact]
    public void AccelerometerOnlyDevicesRetainTheLegacyTiltPath()
    {
        using var rig = new Rig();
        var device = rig.Add(0, gyro: false);
        rig.Tick(device, Vector3.Zero, Rest, 0);
        Assert.Null(rig.Manager.ReadGyroTiltGravity(device.InstanceGuidString, 0));
        Assert.Equal(0, rig.Read(device));
        var pose = Roll(12.5f);
        SourceCoercion.GravityProvider = _ => (pose.X, pose.Y, pose.Z);
        Assert.InRange(rig.Read(device), .499f, .501f);
    }

    [Fact]
    public void GyroCapableDeviceWithoutAnAssignedEntryDoesNotLatchLegacyGravity()
    {
        using var rig = new Rig();
        var device = rig.Add(0);
        rig.Tick(device, Vector3.Zero, Rest, 0);
        SourceCoercion.GravityProvider = _ => throw new InvalidOperationException("Unexpected legacy fallback");
        Assert.Equal(0, rig.Read(device, 1));
        Assert.Equal(0, rig.Read(device, 0));
    }

    [Fact]
    public void SlotsAndDevicesHaveIndependentCalibrationAndState()
    {
        using var rig = new Rig();
        var first = rig.Add(0);
        rig.Assign(first, 1);
        var second = rig.Add(0);
        SourceCoercion.GyroBiasProvider = (guid, slot) => guid == first.InstanceGuidString && slot == 1
            ? (0f, 0f, .2f) : (0f, 0f, 0f);
        rig.Tick(first, Vector3.Zero, Rest, 0);
        rig.Tick(second, Vector3.Zero, Rest, 0);
        Assert.Equal(0, rig.Read(first));
        Assert.Equal(0, rig.Read(first, 1));
        Assert.Equal(0, rig.Read(second));
        for (int i = 1; i <= 100; i++)
        {
            rig.Tick(first, new Vector3(0, 0, .2f), Rest, i * .004);
            rig.Tick(second, Vector3.Zero, Rest, i * .004);
        }
        Assert.True(rig.Read(first) < -.1f);
        Assert.InRange(Math.Abs(rig.Read(first, 1)), 0, .0001f);
        Assert.InRange(Math.Abs(rig.Read(second)), 0, .0001f);
    }

    [Fact]
    public void LongGapAndTransientReadFailurePreserveTheCapturedNeutral()
    {
        using var rig = new Rig();
        var device = rig.Add(0);
        rig.Tick(device, Vector3.Zero, Rest, 0);
        Assert.Equal(0, rig.Read(device));
        long generation = rig.Manager.ReadGyroTiltGravity(device.InstanceGuidString, 0).Value.Generation;
        rig.Tick(device, Vector3.Zero, Roll(20), 1);
        Assert.InRange(rig.Read(device), .799f, .801f);
        Assert.Equal(generation, rig.Manager.ReadGyroTiltGravity(device.InstanceGuidString, 0).Value.Generation);
        rig.Manager.InvalidateGyroTiltGravity(device.InstanceGuid);
        Assert.Equal(0, rig.Read(device));
        rig.Tick(device, Vector3.Zero, Roll(20), 1.01);
        Assert.InRange(rig.Read(device), .799f, .801f);
        Assert.Equal(generation, rig.Manager.ReadGyroTiltGravity(device.InstanceGuidString, 0).Value.Generation);
    }

    [Fact]
    public void CalibrationChangeDoesNotMoveNeutralAndCalibratedRestDoesNotDrift()
    {
        using var rig = new Rig();
        var device = rig.Add(0);
        rig.Tick(device, Vector3.Zero, Rest, 0);
        Assert.Equal(0, rig.Read(device));
        rig.Tick(device, Vector3.Zero, Roll(15), 1);
        Assert.InRange(rig.Read(device), .599f, .601f);
        long generation = rig.Manager.ReadGyroTiltGravity(device.InstanceGuidString, 0).Value.Generation;
        var bias = new Vector3(.012f, -.021f, .015f);
        SourceCoercion.GyroBiasProvider = (_, _) => (bias.X, bias.Y, bias.Z);
        for (int i = 1; i <= 30000; i++) rig.Tick(device, bias, Roll(15), 1 + i * .004);
        Assert.InRange(rig.Read(device), .599f, .601f);
        Assert.Equal(generation, rig.Manager.ReadGyroTiltGravity(device.InstanceGuidString, 0).Value.Generation);
    }

    [Fact]
    public void RealRecenterPathReseedsOnlyTheNamedDevice()
    {
        using var rig = new Rig();
        var first = rig.Add(0);
        var second = rig.Add(1);
        rig.Tick(first, Vector3.Zero, Rest, 0);
        rig.Tick(second, Vector3.Zero, Rest, 0);
        Assert.Equal(0, rig.Read(first));
        Assert.Equal(0, rig.Read(second, 1));
        rig.Tick(first, Vector3.Zero, Roll(20), 1);
        rig.Tick(second, Vector3.Zero, Roll(20), 1);
        using var service = new InputService(new MainViewModel());
        typeof(InputService).GetField("_inputManager", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(service, rig.Manager);
        typeof(InputService).GetMethod("RecenterMotionState", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(service, new object[] { new System.Collections.Generic.List<Guid> { first.InstanceGuid } });
        rig.Tick(first, Vector3.Zero, Roll(20), 1.01);
        Assert.Equal(0, rig.Read(first), 4);
        Assert.InRange(rig.Read(second, 1), .799f, .801f);
    }

    [Fact]
    public void ReopenedWrapperGetsANewPoseGeneration()
    {
        using var rig = new Rig();
        var device = rig.Add(0);
        rig.Tick(device, Vector3.Zero, Rest, 0);
        Assert.Equal(0, rig.Read(device));
        long old = rig.Manager.ReadGyroTiltGravity(device.InstanceGuidString, 0).Value.Generation;
        device.Device.Dispose();
        device.Device = new WebControllerDevice(Guid.NewGuid().ToString(), "Reconnected") { HasGyro = true, HasAccel = true };
        rig.Tick(device, Vector3.Zero, Roll(20), .01);
        Assert.True(rig.Manager.ReadGyroTiltGravity(device.InstanceGuidString, 0).Value.Generation > old);
        Assert.Equal(0, rig.Read(device), 4);
    }

    [Fact]
    public void ConfirmedDisconnectReturnsNeutralWithoutTheLegacyProvider()
    {
        using var rig = new Rig();
        var device = rig.Add(0);
        rig.Tick(device, Vector3.Zero, Rest, 0);
        Assert.Equal(0, rig.Read(device));
        rig.Tick(device, Vector3.Zero, Roll(20), 1);
        SourceCoercion.GravityProvider = _ => throw new InvalidOperationException("Offline used legacy gravity");
        typeof(InputManager).GetMethod("MarkDeviceOffline", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(rig.Manager, new object[] { device });
        Assert.Equal(0, rig.Read(device));
        Assert.True(rig.Manager.ReadGyroTiltGravity(device.InstanceGuidString, 0).HasValue);
    }

    [Fact]
    public void InvalidGyroAndAccelerationRecoverWithoutChangingNeutral()
    {
        using var rig = new Rig();
        var device = rig.Add(0);
        rig.Tick(device, Vector3.Zero, Rest, 0);
        Assert.Equal(0, rig.Read(device));
        var generation = rig.Manager.ReadGyroTiltGravity(device.InstanceGuidString, 0).Value.Generation;
        rig.Tick(device, new Vector3(float.NaN, 0, 0), Rest, .004);
        Assert.Equal(0, rig.Read(device));
        rig.Tick(device, Vector3.Zero, Roll(20), .008);
        Assert.InRange(rig.Read(device), .799f, .801f);
        device.InputState.Accel = null;
        rig.Manager.UpdateGyroTiltGravity(device, device.Device, device.InputState, Stopwatch.Frequency * 2);
        Assert.Equal(0, rig.Read(device));
        device.InputState.Accel = new float[3];
        rig.Tick(device, Vector3.Zero, Vector3.Zero, 1.004);
        Assert.Equal(0, rig.Read(device));
        rig.Tick(device, Vector3.Zero, Roll(20), 1.008);
        Assert.InRange(rig.Read(device), .799f, .801f);
        Assert.Equal(generation, rig.Manager.ReadGyroTiltGravity(device.InstanceGuidString, 0).Value.Generation);
    }

    [Fact]
    public void ProfileResetRecapturesTiltAtTheCurrentPose()
    {
        using var rig = new Rig();
        var device = rig.Add(0);
        rig.Tick(device, Vector3.Zero, Rest, 0);
        Assert.Equal(0, rig.Read(device));
        rig.Tick(device, Vector3.Zero, Roll(20), 1);
        Assert.InRange(rig.Read(device), .799f, .801f);
        using var service = new InputService(new MainViewModel());
        typeof(InputService).GetField("_inputManager", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(service, rig.Manager);
        typeof(InputService).GetMethod("ResetRuntimeStateForProfileSwitch", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(service, null);
        SourceCoercion.GravityProvider = _ => throw new InvalidOperationException("Reset used legacy gravity");
        Assert.Equal(0, rig.Read(device));
        rig.Tick(device, Vector3.Zero, Roll(20), 1.01);
        Assert.Equal(0, rig.Read(device), 4);
    }

    [Fact]
    public void GripRotationAppliesOnceAndTiltAxesShareTheirNewNeutral()
    {
        using var rig = new Rig();
        var device = rig.Add(0);
        SourceCoercion.GyroTuningProvider = (_, _) => new SourceCoercion.GyroTuning { Grip = SourceCoercion.GripSideways };
        rig.Tick(device, Vector3.Zero, Rest, 0);
        Assert.Equal(0, rig.Read(device));
        Assert.Equal(0, rig.Read(device, descriptor: SourceCoercion.GyroTiltYDescriptor));
        // Sideways maps native Z into the horizontal tilt component.
        float rad = 12.5f * MathF.PI / 180;
        rig.Tick(device, Vector3.Zero, new Vector3(0, MathF.Cos(rad), -MathF.Sin(rad)) * 9.81f, 1);
        Assert.InRange(rig.Read(device), .499f, .501f);
        Assert.InRange(Math.Abs(rig.Read(device, descriptor: SourceCoercion.GyroTiltYDescriptor)), 0, .0001f);
        SourceCoercion.GyroTuningProvider = (_, _) => new SourceCoercion.GyroTuning { Grip = SourceCoercion.GripPointing };
        Assert.Equal(0, rig.Read(device), 4);
    }

    [Fact]
    public void RemovingAnAssignmentRetiresItsPose()
    {
        using var rig = new Rig();
        var device = rig.Add(0);
        rig.Tick(device, Vector3.Zero, Rest, 0);
        Assert.Equal(0, rig.Read(device));
        SettingsManager.UserSettings.Items.Clear();
        rig.Manager.ResetGestureContexts(); // refreshes the shared assignment snapshot
        rig.Tick(device, Vector3.Zero, Roll(20), 1);
        Assert.Equal(0, rig.Read(device));
        rig.Assign(device, 1);
        rig.Manager.ResetGestureContexts();
        rig.Tick(device, Vector3.Zero, Roll(20), 1.01);
        Assert.Equal(0, rig.Read(device, 1), 4);
    }

    [Fact]
    public void RemovedDeviceRecordsDoNotRetainRuntimeRegistration()
    {
        using var rig = new Rig();
        var device = rig.Add(0);
        rig.Tick(device, Vector3.Zero, Rest, 0);
        Assert.True(rig.Manager.ReadGyroTiltGravity(device.InstanceGuidString, 0).HasValue);
        SettingsManager.UserDevices.Items.Clear();
        typeof(InputManager).GetMethod("UpdateInputStates", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(rig.Manager, null);
        Assert.Null(rig.Manager.ReadGyroTiltGravity(device.InstanceGuidString, 0));
        device.Device.Dispose();
    }

    [Fact]
    public void StaleGenerationCannotOverwriteANewerNeutral()
    {
        using var rig = new Rig();
        var device = rig.Add(0);
        var pose = Rest;
        long generation = 20;
        SourceCoercion.GyroTiltGravityProvider = (_, _) => new(pose.X, pose.Y, pose.Z, generation);
        Assert.Equal(0, rig.Read(device));
        generation = 21; pose = Roll(10);
        Assert.Equal(0, rig.Read(device), 4);
        generation = 20; pose = Roll(-15);
        Assert.Equal(0, rig.Read(device));
        generation = 21; pose = Roll(20);
        Assert.InRange(rig.Read(device), .399f, .401f);
    }

    [Fact]
    public async Task ResetAndReadCanRunBesideThePollingWriter()
    {
        using var rig = new Rig();
        var device = rig.Add(0);
        rig.Tick(device, Vector3.Zero, Rest, 0);
        await Task.WhenAll(
            Task.Run(() => { for (int i = 1; i <= 1000; i++) rig.Tick(device, Vector3.Zero, Rest, i * .001); }),
            Task.Run(() => { for (int i = 0; i < 1000; i++) rig.Manager.ResetGyroTiltGravity(device.InstanceGuid); }),
            Task.Run(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    var sample = rig.Manager.ReadGyroTiltGravity(device.InstanceGuidString, 0);
                    Assert.True(sample.HasValue);
                    Assert.True(float.IsFinite(sample.Value.X) && float.IsFinite(sample.Value.Y) && float.IsFinite(sample.Value.Z));
                }
            }));
    }

    private sealed class Rig : IDisposable
    {
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly bool[] _created = SettingsManager.SlotCreated;
        private readonly Func<string, (float, float, float)> _gravity = SourceCoercion.GravityProvider;
        private readonly Func<string, int, GyroTiltGravitySample?> _tilt = SourceCoercion.GyroTiltGravityProvider;
        private readonly Func<string, int, (float, float, float)> _bias = SourceCoercion.GyroBiasProvider;
        private readonly Func<string, int, SourceCoercion.GyroTuning> _tuning = SourceCoercion.GyroTuningProvider;
        public InputManager Manager { get; } = new();

        public Rig()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
            SourceCoercion.GravityProvider = _ => (0f, 9.81f, 0f);
            SourceCoercion.GyroTiltGravityProvider = Manager.ReadGyroTiltGravity;
            SourceCoercion.GyroBiasProvider = (_, _) => (0f, 0f, 0f);
            SourceCoercion.GyroTuningProvider = (_, _) => new SourceCoercion.GyroTuning { Grip = "Pointing" };
            SourceCoercion.ResetGyroLeanNeutral();
        }

        public UserDevice Add(int slot, bool gyro = true)
        {
            var wrapper = new WebControllerDevice(Guid.NewGuid().ToString(), "Motion Input") { HasGyro = gyro, HasAccel = true };
            wrapper.SetConnected(true);
            var device = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), Device = wrapper, InputState = new CustomInputState(),
                IsOnline = true, HasGyro = gyro, HasAccel = true, CapType = InputDeviceType.Gamepad,
            };
            SettingsManager.UserDevices.Items.Add(device);
            Assign(device, slot);
            return device;
        }

        public void Assign(UserDevice device, int slot)
            => SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = device.InstanceGuid, MapTo = slot });

        public void Tick(UserDevice device, Vector3 gyro, Vector3 accel, double seconds)
        {
            var state = device.InputState;
            state.Gyro[0] = gyro.X; state.Gyro[1] = gyro.Y; state.Gyro[2] = gyro.Z;
            state.Accel[0] = accel.X; state.Accel[1] = accel.Y; state.Accel[2] = accel.Z;
            Manager.UpdateGyroTiltGravity(device, device.Device, state,
                Stopwatch.Frequency + (long)(seconds * Stopwatch.Frequency));
        }

        public float Read(UserDevice device, int slot = 0, string descriptor = SourceCoercion.GyroTiltXDescriptor)
            => SourceCoercion.EvaluateForBipolarAxisTarget(device.InputState,
                new MappingSource { Descriptor = descriptor, DeviceGuid = device.InstanceGuidString,
                    ParamTiltRangeDeg = 25, ParamTiltInnerDz = 0 }, slot, evaluatedDeviceGuid: device.InstanceGuidString);

        public void Dispose()
        {
            foreach (var device in SettingsManager.UserDevices.Items) device.Device?.Dispose();
            Manager.Dispose();
            SourceCoercion.ResetGyroLeanNeutral();
            SourceCoercion.GravityProvider = _gravity;
            SourceCoercion.GyroTiltGravityProvider = _tilt;
            SourceCoercion.GyroBiasProvider = _bias;
            SourceCoercion.GyroTuningProvider = _tuning;
            SettingsManager.UserDevices = _devices;
            SettingsManager.UserSettings = _settings;
            SettingsManager.SlotCreated = _created;
        }
    }
}
