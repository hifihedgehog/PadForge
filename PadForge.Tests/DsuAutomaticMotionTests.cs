using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    [Collection("SettingsManagerStatics")]
    public class DsuAutomaticMotionTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AllAssignedMotionSources_UseTheExistingAxisReconciliation(bool mapped)
        {
            using var rig = new MotionRig();
            var first = rig.AddDevice(0, 0.1f, -0.8f, 0.2f);
            var second = rig.AddDevice(0, 0.9f, -0.1f, -0.7f);
            rig.ConfigureSlot(0, mapped ? VirtualControllerType.PlayStation : VirtualControllerType.Xbox, first);
            if (mapped)
                MappingSetMigrator.EnsureMotionRows(SettingsManager.SlotMappingSets[0], 1,
                    new[] { (second.InstanceGuidString, true, true) });
            var packets = await rig.TickAndReceive();
            Assert.Equal(0.9f * 180f / MathF.PI, ReadFloat(packets[0], 88), 4);
            Assert.Equal(0.8f * 180f / MathF.PI, ReadFloat(packets[0], 92), 4);
            Assert.Equal(0.7f * 180f / MathF.PI, ReadFloat(packets[0], 96), 4);
            if (mapped)
                Assert.Equal(0.9f * 180f / MathF.PI, rig.Manager.MotionSnapshots[0].GyroPitch, 4);
            else
                Assert.False(rig.Manager.MotionSnapshots[0].HasMotion);
        }

        [Theory]
        [InlineData("MaxAbs", 400f, 0.8f)]
        [InlineData("OR", 400f, 0.8f)]
        [InlineData("AND", 400f, 0.8f)]
        [InlineData("XOR", 400f, 0.8f)]
        [InlineData("StickTrim", 400f, 0.8f)]
        [InlineData("Sum", 700f, 1.5f)]
        [InlineData("Average", 350f, 0.75f)]
        [InlineData("Custom", 1100f, 2.3f)]
        public async Task MotionRows_UseTheirCombineModeWithoutAStickRangeClamp(string mode, float gyro, float accel)
        {
            using var rig = new MotionRig();
            var first = rig.AddDevice(0, 300f * MathF.PI / 180f, 0f, 0f);
            var second = rig.AddDevice(0, 400f * MathF.PI / 180f, 0f, 0f);
            first.InputState.Accel[0] = 0.7f * 9.80665f;
            second.InputState.Accel[0] = 0.8f * 9.80665f;
            rig.ConfigureSlot(0, VirtualControllerType.PlayStation, first);
            MappingSetMigrator.EnsureMotionRows(SettingsManager.SlotMappingSets[0], 1,
                new[] { (second.InstanceGuidString, true, true) });
            foreach (var row in SettingsManager.SlotMappingSets[0].Rows)
            {
                row.CombineMode = mode;
                row.CombineExpression = "s[0] + s[1] * 2";
            }
            var packets = await rig.TickAndReceive();
            Assert.Equal(gyro, ReadFloat(packets[0], 88), 2);
            Assert.Equal(-accel, ReadFloat(packets[0], 76), 4);
            Assert.Equal(gyro, rig.Manager.MotionSnapshots[0].GyroPitch, 2);
            Assert.Equal(accel, rig.Manager.MotionSnapshots[0].AccelX, 4);
        }

        [Theory]
        [InlineData("MaxAbs", 0.4f)]
        [InlineData("Sum", 0.2f)]
        [InlineData("Average", 0.1f)]
        public async Task SourceInversion_IsAppliedBeforeReconciliation(string mode, float expected)
        {
            using var rig = new MotionRig();
            var first = rig.AddDevice(0, 0.2f, 0f, 0f);
            var second = rig.AddDevice(0, 0.4f, 0f, 0f);
            rig.ConfigureSlot(0, VirtualControllerType.PlayStation, first);
            var row = SettingsManager.SlotMappingSets[0].Rows[0];
            row.Sources[0].Invert = true;
            row.Sources.Add(Source(second, MappingSetMigrator.MotionGyroSourceDescriptor));
            row.CombineMode = mode;
            Assert.Equal(expected * 180f / MathF.PI, ReadFloat((await rig.TickAndReceive())[0], 88), 4);
        }

        [Fact]
        public async Task CustomMotion_PreservesOfflineSourcePositions()
        {
            using var rig = new MotionRig();
            var offline = rig.AddDevice(0, 5f, 5f, 5f, online: false);
            var online = rig.AddDevice(0, 0.4f, 0f, 0f);
            rig.ConfigureSlot(0, VirtualControllerType.PlayStation, offline);
            var row = SettingsManager.SlotMappingSets[0].Rows[0];
            row.Sources.Add(Source(online, MappingSetMigrator.MotionGyroSourceDescriptor));
            row.CombineMode = "Custom";
            row.CombineExpression = "s[1] - s[0] * 2";
            Assert.Equal(0.4f * 180f / MathF.PI, ReadFloat((await rig.TickAndReceive())[0], 88), 4);
        }

        [Fact]
        public async Task AnyDeviceMotion_ReconcilesAssignedDevicesAsOneSourcePosition()
        {
            using var rig = new MotionRig();
            var first = rig.AddDevice(0, 0.2f, 0f, 0f);
            rig.AddDevice(0, 0.8f, 0f, 0f);
            rig.AddDevice(0, 9f, 9f, 9f, gyro: false, accel: false);
            rig.ConfigureSlot(0, VirtualControllerType.PlayStation, first);
            var row = SettingsManager.SlotMappingSets[0].Rows[0];
            row.Sources[0].DeviceGuid = "";
            row.Sources.Add(Source(first, MappingSetMigrator.MotionGyroSourceDescriptor));
            row.CombineMode = "Sum";
            // Like other rows, Any Device is one source value. Listing an
            // explicit source beside it deliberately adds another contribution.
            Assert.Equal(1f * 180f / MathF.PI, ReadFloat((await rig.TickAndReceive())[0], 88), 4);
        }

        [Fact]
        public async Task EveryDevice_UsesItsOwnCalibrationAndGripBeforeReconciliation()
        {
            using var rig = new MotionRig();
            var first = rig.AddDevice(0, 0.1f, 0.8f, 0.3f);
            var second = rig.AddDevice(0, 0.4f, 0.5f, 0.6f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, first);
            SourceCoercion.GyroBiasProvider = (id, _) => id == first.InstanceGuidString
                ? (0.1f, 0f, 0f) : (0f, 0.2f, 0f);
            SourceCoercion.GyroTuningProvider = (id, _) => new SourceCoercion.GyroTuning
            {
                Grip = id == first.InstanceGuidString ? "Pointing" : "Sideways",
                ApplyToPassthrough = false,
            };
            var packet = (await rig.TickAndReceive())[0];
            Assert.Equal(0.6f * 180f / MathF.PI, ReadFloat(packet, 88), 4);
            Assert.Equal(-0.8f * 180f / MathF.PI, ReadFloat(packet, 92), 4);
            Assert.Equal(0.4f * 180f / MathF.PI, ReadFloat(packet, 96), 4);
        }

        [Fact]
        public async Task IdleFirstDevice_DoesNotBlockMotionFromTheSecond()
        {
            using var rig = new MotionRig();
            var first = rig.AddDevice(0, 0f, 0f, 0f);
            var second = rig.AddDevice(0, 0f, 0.5f, 0f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, first);
            var packet = (await rig.TickAndReceive())[0];
            Assert.Equal(-0.5f * 180f / MathF.PI, ReadFloat(packet, 92), 4);
            second.InputState.Gyro[1] = 0f;
            first.InputState.Gyro[1] = -0.3f;
            packet = (await rig.TickAndReceive())[0];
            Assert.Equal(0.3f * 180f / MathF.PI, ReadFloat(packet, 92), 4);
        }

        [Fact]
        public async Task DisabledOrInvalidSource_DoesNotBlockAnotherAssignedDevice()
        {
            using var rig = new MotionRig();
            var first = rig.AddDevice(0, 5f, 5f, 5f);
            rig.AddDevice(0, 0.4f, 0f, 0f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, first);
            SettingsManager.UserSettings.Items[0].IsEnabled = false;
            Assert.Equal(0.4f * 180f / MathF.PI, ReadFloat((await rig.TickAndReceive())[0], 88), 4);
            SettingsManager.UserSettings.Items[0].IsEnabled = true;
            first.InputState.Gyro[0] = float.NaN;
            Assert.Equal(0.4f * 180f / MathF.PI, ReadFloat((await rig.TickAndReceive())[0], 88), 4);
        }

        [Fact]
        public async Task MissingSettings_ClearsPreviouslyPublishedMotion()
        {
            using var rig = new MotionRig();
            var first = rig.AddDevice(0, 0.2f, 0.3f, 0.4f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, first);
            await rig.TickAndReceive();
            SettingsManager.UserSettings = null;
            var packet = (await rig.TickAndReceive())[0];
            Assert.Equal(0, packet[22]);
            Assert.Equal(0f, ReadFloat(packet, 88));
        }

        [Fact]
        public async Task MotionLayers_KeepTheirExistingPriorityAndBaseFallback()
        {
            InputManager.ClearAllShiftRuntime();
            try
            {
                using var rig = new MotionRig();
                var first = rig.AddDevice(0, 0.2f, 0f, 0f);
                var second = rig.AddDevice(0, 0.4f, 0f, 0f);
                rig.ConfigureSlot(0, VirtualControllerType.PlayStation, first);
                var set = SettingsManager.SlotMappingSets[0];
                var layer = new MappingRow
                {
                    Target = MappingSetMigrator.MotionGyroTarget, LayerMask = "View",
                    CombineMode = "Sum", NoInherit = true,
                    Sources = new()
                    {
                        Source(first, MappingSetMigrator.MotionGyroSourceDescriptor),
                        Source(second, MappingSetMigrator.MotionGyroSourceDescriptor),
                    },
                };
                set.Rows.Add(layer);
                set.ShiftActivators.Add(new ShiftActivator
                {
                    DeviceGuid = "", Descriptor = "Button 28", Mode = "Hold",
                    LayerMask = "View", LayerName = "View", Kind = "Button",
                    InheritUnmapped = false, DelayMs = 0, AutoCancelMs = 0,
                });
                first.InputState.Buttons[28] = true;
                Assert.Equal("View", InputManager.ResolveActiveLayerMask(0, set, first.InputState, ""));
                Assert.Equal(0.6f * 180f / MathF.PI, ReadFloat((await rig.TickAndReceive())[0], 88), 4);
                layer.Sources.Clear();
                // Motion retains the established Base fallback even when a
                // replace layer has no usable motion source.
                Assert.Equal(0.2f * 180f / MathF.PI, ReadFloat((await rig.TickAndReceive())[0], 88), 4);
            }
            finally
            {
                InputManager.ClearAllShiftRuntime();
            }
        }

        [Fact]
        public async Task MaxAbsTie_UsesTheSameFirstContributionRuleAsOtherAxes()
        {
            using var rig = new MotionRig();
            var first = rig.AddDevice(0, 0.5f, 0f, 0f);
            rig.AddDevice(0, -0.5f, 0f, 0f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, first);
            Assert.Equal(0.5f * 180f / MathF.PI, ReadFloat((await rig.TickAndReceive())[0], 88), 4);
            SettingsManager.UserSettings.Items.Reverse();
            Assert.Equal(-0.5f * 180f / MathF.PI, ReadFloat((await rig.TickAndReceive())[0], 88), 4);
        }

        [Fact]
        public async Task CustomOverflow_DoesNotSendNonFiniteMotion()
        {
            using var rig = new MotionRig();
            var first = rig.AddDevice(0, 0.2f, 0f, 0f);
            rig.ConfigureSlot(0, VirtualControllerType.PlayStation, first);
            var row = SettingsManager.SlotMappingSets[0].Rows[0];
            row.CombineMode = "Custom";
            row.CombineExpression = "1000000000 * 1000000000 * 1000000000 * 1000000000 * 1000000000";
            Assert.True(MappingExpression.Compile(row.CombineExpression).IsValid);
            Assert.Equal(0f, ReadFloat((await rig.TickAndReceive())[0], 88));
        }

        [Theory]
        [InlineData(VirtualControllerType.Xbox)]
        [InlineData(VirtualControllerType.Extended)]
        [InlineData(VirtualControllerType.Midi)]
        [InlineData(VirtualControllerType.KeyboardMouse)]
        public async Task UnmappedSlot_StreamsItsDeviceBesideAMappedControl(VirtualControllerType type)
        {
            using var rig = new MotionRig();
            var device = rig.AddDevice(0, 0.25f, -0.5f, 0.75f);
            rig.ConfigureSlot(0, type, device);
            rig.Assign(device, 1);
            rig.ConfigureSlot(1, VirtualControllerType.PlayStation, device);
            Assert.Empty(SettingsManager.SlotMappingSets[0].Rows);
            Assert.Equal(2, SettingsManager.SlotMappingSets[1].Rows.Count);

            var packets = await rig.TickAndReceive();
            // Same sensor, same poll, different output families. The mapped
            // slot proves that acquisition, encoding and UDP delivery ran.
            Assert.Equal(2, packets[1][22]);
            Assert.True(rig.Manager.MotionSnapshots[1].HasMotion);
            Assert.False(rig.Manager.MotionSnapshots[0].HasMotion);
            Assert.Equal(2, packets[0][21]); // connected
            Assert.Equal(2, packets[0][22]); // full motion model
            foreach (int offset in new[] { 76, 80, 84, 88, 92, 96 })
                Assert.Equal(ReadFloat(packets[1], offset), ReadFloat(packets[0], offset));
            Assert.Equal(0.25f * 180f / MathF.PI, ReadFloat(packets[0], 88), 4);
            Assert.Equal(0.5f * 180f / MathF.PI, ReadFloat(packets[0], 92), 4);
            Assert.Equal(-0.75f * 180f / MathF.PI, ReadFloat(packets[0], 96), 4);
            Assert.Empty(SettingsManager.SlotMappingSets[0].Rows);
        }

        [Theory]
        [InlineData("Pointing", false)]
        [InlineData("Sideways", false)]
        [InlineData("WiiWheel", false)]
        [InlineData("Upright", false)]
        [InlineData("Pointing", true)]
        [InlineData("Sideways", true)]
        [InlineData("WiiWheel", true)]
        [InlineData("Upright", true)]
        public async Task AutomaticAndMappedMotion_HaveTheSameCalibrationGripAndTuning(string grip, bool tuned)
        {
            using var rig = new MotionRig();
            var device = rig.AddDevice(0, 0.4f, -0.6f, 0.8f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, device);
            rig.Assign(device, 1);
            rig.ConfigureSlot(1, VirtualControllerType.PlayStation, device);
            SourceCoercion.GyroBiasProvider = (_, _) => (0.1f, -0.2f, 0.3f);
            SourceCoercion.GyroTuningProvider = (_, _) => new SourceCoercion.GyroTuning
            {
                Grip = grip, ApplyToPassthrough = false,
            };
            var baseline = await rig.TickAndReceive();
            SourceCoercion.GyroTuningProvider = (_, _) => new SourceCoercion.GyroTuning
            {
                Grip = grip, ApplyToPassthrough = tuned, Space = "Local",
                SensH = 1.5f, SensV = 0.5f, InvertPitch = true,
            };
            var packets = await rig.TickAndReceive();
            Assert.Equal(2, packets[0][22]);
            Assert.Equal(2, packets[1][22]);
            foreach (int offset in new[] { 76, 80, 84, 88, 92, 96 })
                Assert.Equal(ReadFloat(packets[1], offset), ReadFloat(packets[0], offset));
            var expectedAccel = grip switch
            {
                "Sideways" => (-0.75f, -0.5f, 0.25f),
                "WiiWheel" => (-0.75f, -0.25f, -0.5f),
                "Upright" => (-0.25f, 0.75f, -0.5f),
                _ => (-0.25f, -0.5f, -0.75f),
            };
            Assert.Equal(expectedAccel.Item1, ReadFloat(packets[0], 76), 5);
            Assert.Equal(expectedAccel.Item2, ReadFloat(packets[0], 80), 5);
            Assert.Equal(expectedAccel.Item3, ReadFloat(packets[0], 84), 5);
            if (tuned)
                Assert.NotEqual(ReadFloat(baseline[0], 88), ReadFloat(packets[0], 88));
        }

        [Theory]
        [InlineData(VirtualControllerType.PlayStation)]
        [InlineData(VirtualControllerType.Nintendo)]
        public async Task RemovingMotionRows_RestoresAutomaticDsuWithoutRestoringHidMotion(VirtualControllerType type)
        {
            using var rig = new MotionRig();
            var device = rig.AddDevice(0, 0.2f, 0.3f, 0.4f);
            rig.ConfigureSlot(0, type, device);
            var mapped = await rig.TickAndReceive();
            Assert.True(rig.Manager.MotionSnapshots[0].HasMotion);
            SettingsManager.SlotMappingSets[0].Rows.Clear();
            var automatic = await rig.TickAndReceive();
            Assert.False(rig.Manager.MotionSnapshots[0].HasMotion);
            Assert.Equal(2, automatic[0][22]);
            foreach (int offset in new[] { 76, 80, 84, 88, 92, 96 })
                Assert.Equal(ReadFloat(mapped[0], offset), ReadFloat(automatic[0], offset));
            Assert.Empty(SettingsManager.SlotMappingSets[0].Rows);
        }

        [Fact]
        public async Task AutomaticSources_ReconcileAllAvailableSensorChannels()
        {
            using var rig = new MotionRig();
            var accelOnly = rig.AddDevice(0, 8f, 8f, 8f, gyro: false);
            var gyroOnly = rig.AddDevice(0, 9f, 9f, 9f, accel: false);
            var full = rig.AddDevice(0, 0.1f, 0.2f, 0.3f);
            accelOnly.InputState.Accel[0] = 50f;
            gyroOnly.InputState.Accel[0] = 60f;
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, full);
            var packets = await rig.TickAndReceive();
            Assert.Equal(-50f / 9.80665f, ReadFloat(packets[0], 76), 5);
            Assert.Equal(9f * 180f / MathF.PI, ReadFloat(packets[0], 88), 4);
        }

        [Fact]
        public async Task AutomaticSources_TrackEveryOnlineAndAssignedDevice()
        {
            using var rig = new MotionRig();
            var first = rig.AddDevice(0, 0.3f, 0f, 0f);
            var second = rig.AddDevice(0, 0.2f, 0f, 0f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, first);
            SettingsManager.UserDevices.Items.Reverse();
            var packets = await rig.TickAndReceive();
            Assert.Equal(0.3f * 180f / MathF.PI, ReadFloat(packets[0], 88), 4);
            first.IsOnline = false;
            packets = await rig.TickAndReceive();
            Assert.Equal(0.2f * 180f / MathF.PI, ReadFloat(packets[0], 88), 4);
            first.IsOnline = true;
            packets = await rig.TickAndReceive();
            Assert.Equal(0.3f * 180f / MathF.PI, ReadFloat(packets[0], 88), 4);
            SettingsManager.UserSettings.Items.RemoveAll(s => s.InstanceGuid == first.InstanceGuid);
            packets = await rig.TickAndReceive();
            Assert.Equal(0.2f * 180f / MathF.PI, ReadFloat(packets[0], 88), 4);
            second.IsOnline = false;
            packets = await rig.TickAndReceive();
            Assert.Equal(0, packets[0][21]);
            Assert.Equal(0, packets[0][22]);
            Assert.Equal(0f, ReadFloat(packets[0], 88));
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public async Task PartialSensors_DoNotReadAnUnsupportedChannel(bool gyro, bool accel)
        {
            using var rig = new MotionRig();
            var device = rig.AddDevice(0, 0.1f, 0.2f, 0.3f, gyro, accel);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, device);
            var packets = await rig.TickAndReceive();
            Assert.Equal(gyro || accel ? 2 : 0, packets[0][22]);
            Assert.Equal(gyro ? 0.1f * 180f / MathF.PI : 0f, ReadFloat(packets[0], 88), 4);
            Assert.Equal(accel ? -0.25f : 0f, ReadFloat(packets[0], 76), 5);
        }

        [Theory]
        [InlineData(VirtualControllerType.Xbox)]
        [InlineData(VirtualControllerType.PlayStation)]
        public async Task MissingSamples_DoNotAdvertiseMotion(VirtualControllerType type)
        {
            using var rig = new MotionRig();
            var device = rig.AddDevice(0, 0.1f, 0.2f, 0.3f);
            rig.ConfigureSlot(0, type, device);
            device.InputState.Gyro = null;
            device.InputState.Accel = new float[2];
            var packets = await rig.TickAndReceive();
            Assert.Equal(0, packets[0][22]);
        }

        [Fact]
        public async Task ExplicitEmptyOrOfflineRows_DoNotFallBackToAnotherDevice()
        {
            using var rig = new MotionRig();
            var online = rig.AddDevice(0, 0.3f, 0.4f, 0.5f);
            var offline = rig.AddDevice(0, 1f, 2f, 3f, online: false);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, online);
            Assert.Equal(2, (await rig.TickAndReceive())[0][22]);
            var row = new MappingRow { Target = MappingSetMigrator.MotionGyroTarget };
            SettingsManager.SlotMappingSets[0].Rows.Add(row);
            Assert.Equal(0, (await rig.TickAndReceive())[0][22]);
            row.Sources.Add(Source(offline, MappingSetMigrator.MotionGyroSourceDescriptor));
            Assert.Equal(0, (await rig.TickAndReceive())[0][22]);
        }

        [Fact]
        public async Task ReplacingAProfile_UsesItsMappedSourcesAndInversion()
        {
            using var rig = new MotionRig();
            var first = rig.AddDevice(0, 0.1f, 0.2f, 0.3f);
            var second = rig.AddDevice(0, 0.4f, 0.5f, 0.6f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, first);
            await rig.TickAndReceive();
            SettingsManager.SlotMappingSets[0] = new MappingSet
            {
                Rows = new List<MappingRow>
                {
                    new() { Target = MappingSetMigrator.MotionGyroTarget,
                        Sources = new() { Source(second, MappingSetMigrator.MotionGyroSourceDescriptor, true) } },
                    new() { Target = MappingSetMigrator.MotionAccelTarget,
                        Sources = new() { Source(first, MappingSetMigrator.MotionAccelSourceDescriptor) } },
                },
            };
            var packets = await rig.TickAndReceive();
            Assert.Equal(-0.4f * 180f / MathF.PI, ReadFloat(packets[0], 88), 4);
            Assert.Equal(-0.25f, ReadFloat(packets[0], 76), 5);
            Assert.True(rig.Manager.MotionSnapshots[0].HasMotion);
            SettingsManager.SlotMappingSets[0] = null;
            packets = await rig.TickAndReceive();
            Assert.Equal(0.4f * 180f / MathF.PI, ReadFloat(packets[0], 88), 4);
            Assert.False(rig.Manager.MotionSnapshots[0].HasMotion);
        }

        [Fact]
        public async Task AuxiliaryOnlySensor_UsesItsOwnGyroAndAccelerometer()
        {
            using var rig = new MotionRig();
            var device = rig.AddDevice(0, 8f, 9f, 10f, gyro: false, accel: false);
            var wrapper = (MotionDevice)device.Device;
            wrapper.HasGyroAux = wrapper.HasAccelAux = true;
            device.InputState.GyroAux = new[] { 0.1f, 0.2f, 0.3f };
            device.InputState.AccelAux = new[] { 9.80665f, 0f, 0f };
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, device);
            var packets = await rig.TickAndReceive();
            Assert.Equal(2, packets[0][22]);
            Assert.Equal(0.1f * 180f / MathF.PI, ReadFloat(packets[0], 88), 4);
            Assert.Equal(-1f, ReadFloat(packets[0], 76), 5);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task AutomaticSources_UsePrimaryOrAvailableAuxiliaryChannels(bool bodyIsComplete)
        {
            using var rig = new MotionRig();
            var body = rig.AddDevice(0, 0.2f, 0.3f, 0.4f, accel: bodyIsComplete);
            var auxiliary = bodyIsComplete ? body : rig.AddDevice(0, 9f, 9f, 9f, gyro: false, accel: false);
            var wrapper = (MotionDevice)auxiliary.Device;
            wrapper.HasGyroAux = wrapper.HasAccelAux = true;
            auxiliary.InputState.GyroAux = new[] { 0.7f, 0.8f, 0.9f };
            auxiliary.InputState.AccelAux = new[] { 9.80665f, 0f, 0f };
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, body);
            var packets = await rig.TickAndReceive();
            Assert.Equal((bodyIsComplete ? 0.2f : 0.7f) * 180f / MathF.PI, ReadFloat(packets[0], 88), 4);
            Assert.Equal(bodyIsComplete ? -0.25f : -1f, ReadFloat(packets[0], 76), 5);
        }

        [Fact]
        public async Task UnassigningTheLastDevice_ClearsBothSnapshotsAndThePacket()
        {
            using var rig = new MotionRig();
            var device = rig.AddDevice(0, 0.1f, 0.2f, 0.3f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, device);
            await rig.TickAndReceive();
            SettingsManager.UserSettings.Items.Clear();
            var packets = await rig.TickAndReceive();
            Assert.False(rig.Manager.MotionSnapshots[0].HasMotion);
            Assert.False(rig.Manager.DsuMotionSnapshots[0].HasMotion);
            Assert.Equal(0, packets[0][21]);
            Assert.Equal(0, packets[0][22]);
            Assert.Equal(0f, ReadFloat(packets[0], 88));
        }

        [Fact]
        public async Task DisabledSlot_SendsZeroSensorsBesideAnEnabledControl()
        {
            using var rig = new MotionRig();
            var a = rig.AddDevice(0, 0.2f, 0.3f, 0.4f);
            var b = rig.AddDevice(1, 0.6f, 0.7f, 0.8f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, a);
            rig.ConfigureSlot(1, VirtualControllerType.Xbox, b);
            var active = await rig.TickAndReceive();
            Assert.Equal(2, active[0][21]);
            Assert.Equal(2, active[0][22]);
            Assert.NotEqual(0f, ReadFloat(active[0], 88));
            Assert.NotEqual(0f, ReadFloat(active[1], 88));

            SettingsManager.SlotEnabled[0] = false;
            a.InputState.Gyro[0] = 1.2f;
            a.InputState.Accel[0] = 19.6133f;
            var disabled = await rig.TickAndReceive();
            Assert.Equal(0, disabled[0][21]);
            Assert.Equal(0, disabled[0][22]);
            Assert.Equal(0, disabled[0][31]);
            foreach (int offset in new[] { 76, 80, 84, 88, 92, 96 })
            {
                Assert.Equal(0f, ReadFloat(disabled[0], offset));
                Assert.Equal(ReadFloat(active[1], offset), ReadFloat(disabled[1], offset));
            }
            Assert.Equal(2, disabled[1][21]);
            Assert.Equal(2, disabled[1][22]);

            SettingsManager.SlotEnabled[0] = true;
            var resumed = await rig.TickAndReceive();
            Assert.Equal(2, resumed[0][21]);
            Assert.Equal(2, resumed[0][22]);
            Assert.Equal(1.2f * 180f / MathF.PI, ReadFloat(resumed[0], 88), 4);
        }

        [Fact]
        public async Task DisabledRemovedAndNeutralizedSlots_DoNotLeaveAnActiveMotionSample()
        {
            using var rig = new MotionRig();
            var device = rig.AddDevice(0, 0.1f, 0.2f, 0.3f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, device);
            await rig.TickAndReceive();
            SettingsManager.SlotEnabled[0] = false;
            Assert.Equal(0, (await rig.TickAndReceive())[0][21]);
            SettingsManager.SlotEnabled[0] = true;
            SettingsManager.SlotCreated[0] = false;
            Assert.Equal(0, (await rig.TickAndReceive())[0][22]);
            Assert.False(rig.Manager.DsuMotionSnapshots[0].HasMotion);
            SettingsManager.SlotCreated[0] = true;
            await rig.TickAndReceive();
            typeof(InputManager).GetMethod("NeutralizeCombinedOutputs",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(rig.Manager, null);
            Assert.False(rig.Manager.DsuMotionSnapshots[0].HasMotion);
            Assert.Equal(0f, rig.Manager.DsuMotionSnapshots[0].GyroPitch);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task FocusPolicy_LeavesLiveMotionAloneUnlessSuspensionApplies(bool suspend, bool foreground)
        {
            using var rig = new MotionRig();
            var device = rig.AddDevice(0, 0.2f, 0.3f, 0.4f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, device);
            var packet = (await rig.TickAndReceive())[0];
            Assert.NotEqual(0f, ReadFloat(packet, 88));
            int submissions = 0, releases = 0;
            rig.Manager.SuspendWhenBackground = suspend;
            rig.Manager.HostIsForeground = foreground;

            Assert.False(rig.Manager.ApplyFocusSuspension(() => submissions++, () => releases++));

            Assert.Equal(0, submissions);
            Assert.Equal(0, releases);
            Assert.True(rig.Manager.DsuMotionSnapshots[0].HasMotion);
            Assert.Equal(ReadFloat(packet, 88), rig.Manager.DsuMotionSnapshots[0].GyroPitch);
        }

        [Fact]
        public async Task FocusSuspension_DeliversNeutralPacketsAndRearmsAfterResume()
        {
            using var rig = new MotionRig();
            var device = rig.AddDevice(0, 0.2f, 0.3f, 0.4f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, device);
            Assert.NotEqual(0f, ReadFloat((await rig.TickAndReceive())[0], 88));
            int submissions = 0, releases = 0;
            rig.Manager.SuspendWhenBackground = true;
            rig.Manager.HostIsForeground = false;

            Assert.True(rig.Manager.ApplyFocusSuspension(() => submissions++, () => releases++));
            Assert.Equal(2, submissions);
            Assert.Equal(1, releases);
            var firstNeutral = await rig.ReceivePackets();
            AssertNeutralDsuPackets(firstNeutral);
            Assert.True(BinaryPrimitives.ReadInt64LittleEndian(firstNeutral[0].AsSpan(68)) > 0);
            Assert.Equal(0, rig.Manager.DsuMotionSnapshots[0].TimestampUs);

            device.InputState.Gyro[0] = 0.9f;
            await Task.Delay(2);
            Assert.True(rig.Manager.ApplyFocusSuspension(() => submissions++, () => releases++));
            Assert.Equal(3, submissions);
            Assert.Equal(1, releases);
            var repeatedNeutral = await rig.ReceivePackets();
            AssertNeutralDsuPackets(repeatedNeutral);
            AssertDsuTimeAdvanced(firstNeutral, repeatedNeutral);
            Assert.Equal(0, rig.Manager.DsuMotionSnapshots[0].TimestampUs);

            rig.Manager.HostIsForeground = true;
            Assert.False(rig.Manager.ApplyFocusSuspension(() => submissions++, () => releases++));
            Assert.Equal(0.9f * 180f / MathF.PI, ReadFloat((await rig.TickAndReceive())[0], 88), 4);
            rig.Manager.HostIsForeground = false;
            Assert.True(rig.Manager.ApplyFocusSuspension(() => submissions++, () => releases++));
            Assert.Equal(2, releases);
            AssertNeutralDsuPackets(await rig.ReceivePackets());
        }

        [Fact]
        public async Task FocusSuspension_DeliversNeutralEvenWhenControllerSubmissionThrows()
        {
            using var rig = new MotionRig();
            var device = rig.AddDevice(0, 0.2f, 0.3f, 0.4f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, device);
            Assert.NotEqual(0f, ReadFloat((await rig.TickAndReceive())[0], 88));
            rig.Manager.SuspendWhenBackground = true;
            rig.Manager.HostIsForeground = false;
            int releases = 0;

            Assert.True(rig.Manager.ApplyFocusSuspension(
                () => throw new InvalidOperationException("Controller submission test"), () => releases++));

            Assert.Equal(1, releases);
            AssertNeutralDsuPackets(await rig.ReceivePackets());
            Assert.True(rig.Manager.ApplyFocusSuspension(
                () => throw new InvalidOperationException("Controller upkeep test"), () => releases++));
            Assert.Equal(1, releases);
            AssertNeutralDsuPackets(await rig.ReceivePackets());
        }

        private static void AssertNeutralDsuPackets(Dictionary<int, byte[]> packets)
        {
            Assert.Equal(4, packets.Count);
            foreach (var packet in packets.Values)
            {
                Assert.Equal(0, packet[22]);
                foreach (int offset in new[] { 76, 80, 84, 88, 92, 96 })
                    Assert.Equal(0f, ReadFloat(packet, offset));
            }
        }

        private static void AssertDsuTimeAdvanced(Dictionary<int, byte[]> first, Dictionary<int, byte[]> second)
        {
            foreach (var slot in first.Keys)
            {
                Assert.True(BinaryPrimitives.ReadInt64LittleEndian(second[slot].AsSpan(68))
                    > BinaryPrimitives.ReadInt64LittleEndian(first[slot].AsSpan(68)));
                Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(second[slot].AsSpan(32))
                    > BinaryPrimitives.ReadUInt32LittleEndian(first[slot].AsSpan(32)));
            }
        }
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task LastSlotDisableOrDelete_CanIdleBeforeAnotherMotionPoll(bool delete, bool liveController)
        {
            using var rig = new MotionRig();
            var device = rig.AddDevice(0, 0.2f, 0.3f, 0.4f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, device);
            using var policy = new IdlePolicyRig(rig.Manager);
            var controller = liveController ? new IdleController() : null;
            rig.Manager.GetVirtualControllers()[0] = controller;
            rig.Manager.HmInactivityTimeoutSeconds = 60;
            policy.Refresh();
            Assert.False(rig.Manager.IsIdle);
            var moving = await rig.TickAndReceive();
            Assert.NotEqual(0f, ReadFloat(moving[0], 88));
            long cachedTime = rig.Manager.DsuMotionSnapshots[0].TimestampUs;

            if (delete) policy.Devices.DeleteSlot(0);
            else policy.Devices.SetSlotEnabled(0, false);
            policy.Refresh();

            Assert.True(rig.Manager.IsIdle);
            Assert.Same(controller, rig.Manager.GetVirtualControllers()[0]);
            Assert.True(rig.Manager.DsuMotionSnapshots[0].HasMotion);
            Assert.Equal(cachedTime, rig.Manager.DsuMotionSnapshots[0].TimestampUs);
            Assert.True(rig.Manager.BeginIdlePoll());
            var neutral = await rig.ReceivePackets();
            AssertNeutralDsuPackets(neutral);
            foreach (var packet in neutral.Values)
            {
                Assert.Equal(0, packet[21]);
                Assert.Equal(0, packet[31]);
                Assert.True(BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(68)) > 0);
            }
            Assert.Equal(cachedTime, rig.Manager.DsuMotionSnapshots[0].TimestampUs);
            await Task.Delay(2);
            Assert.True(rig.Manager.BeginIdlePoll());
            var repeated = await rig.ReceivePackets();
            AssertNeutralDsuPackets(repeated);
            AssertDsuTimeAdvanced(neutral, repeated);
        }

        [Fact]
        public async Task AnotherActiveSlot_KeepsTheNormalMotionPathAvailable()
        {
            using var rig = new MotionRig();
            var first = rig.AddDevice(0, 0.2f, 0.3f, 0.4f);
            var second = rig.AddDevice(1, 0.6f, 0.7f, 0.8f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, first);
            rig.ConfigureSlot(1, VirtualControllerType.Xbox, second);
            using var policy = new IdlePolicyRig(rig.Manager);
            Assert.NotEqual(0f, ReadFloat((await rig.TickAndReceive())[0], 88));

            policy.Devices.SetSlotEnabled(0, false);
            policy.Refresh();

            Assert.False(rig.Manager.IsIdle);
            Assert.False(rig.Manager.BeginIdlePoll());
            Assert.Equal(0, rig.AvailablePacketBytes);
            var packets = await rig.TickAndReceive();
            Assert.Equal(0f, ReadFloat(packets[0], 88));
            Assert.Equal(0.6f * 180f / MathF.PI, ReadFloat(packets[1], 88), 4);
        }

        [Theory]
        [InlineData(60, true, false, false)]
        [InlineData(60, false, true, false)]
        [InlineData(0, true, true, false)]
        [InlineData(60, true, false, true)]
        [InlineData(60, false, true, true)]
        [InlineData(0, true, true, true)]
        public async Task LastAssignedDeviceLeaves_OnlyWaitsForAnEnabledControllerWithATimeout(
            int timeout, bool liveController, bool expectedIdle, bool unassign)
        {
            using var rig = new MotionRig();
            var device = rig.AddDevice(0, 0.2f, 0.3f, 0.4f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, device);
            using var policy = new IdlePolicyRig(rig.Manager);
            if (liveController) rig.Manager.GetVirtualControllers()[0] = new IdleController();
            rig.Manager.HmInactivityTimeoutSeconds = timeout;
            Assert.NotEqual(0f, ReadFloat((await rig.TickAndReceive())[0], 88));

            if (unassign) policy.Devices.UnassignDevice(device.InstanceGuid);
            else device.IsOnline = false;
            policy.Refresh();

            Assert.Equal(expectedIdle, rig.Manager.IsIdle);
            Assert.Equal(expectedIdle, rig.Manager.BeginIdlePoll());
            var packets = expectedIdle ? await rig.ReceivePackets() : await rig.TickAndReceive();
            AssertNeutralDsuPackets(packets);
        }

        [Fact]
        public async Task IdleWakeBeforeTheUiRefresh_DoesNotRepublishCachedMotion()
        {
            using var rig = new MotionRig();
            var device = rig.AddDevice(0, 0.2f, 0.3f, 0.4f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, device);
            using var policy = new IdlePolicyRig(rig.Manager);
            rig.Manager.HmInactivityTimeoutSeconds = 0;
            Assert.NotEqual(0f, ReadFloat((await rig.TickAndReceive())[0], 88));
            device.IsOnline = false;
            policy.Refresh();
            Assert.True(rig.Manager.IsIdle);

            device.IsOnline = true;
            device.InputState.Gyro[0] = 0.9f;
            Assert.True(rig.Manager.BeginIdlePoll());
            var waiting = await rig.ReceivePackets();
            Assert.Equal(2, waiting[0][21]);
            AssertNeutralDsuPackets(waiting);

            policy.Refresh();
            Assert.False(rig.Manager.BeginIdlePoll());
            Assert.Equal(0, rig.AvailablePacketBytes);
            Assert.Equal(0.9f * 180f / MathF.PI, ReadFloat((await rig.TickAndReceive())[0], 88), 4);
        }

        [Fact]
        public async Task IdleDelivery_ReadsTheCurrentServerAfterPauseAndReplacement()
        {
            using var rig = new MotionRig();
            var device = rig.AddDevice(0, 0.2f, 0.3f, 0.4f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, device);
            using var policy = new IdlePolicyRig(rig.Manager);
            Assert.NotEqual(0f, ReadFloat((await rig.TickAndReceive())[0], 88));
            policy.Devices.SetSlotEnabled(0, false);
            policy.Refresh();
            Assert.True(rig.Manager.BeginIdlePoll());
            AssertNeutralDsuPackets(await rig.ReceivePackets());

            var oldServer = rig.Manager.DsuServer;
            rig.Manager.DsuServer = null;
            oldServer.Stop();
            Assert.True(rig.Manager.BeginIdlePoll());
            Assert.Equal(0, rig.AvailablePacketBytes);

            using var replacement = new DsuMotionServer();
            try
            {
                Assert.True(replacement.Start(0));
                rig.UseServer(replacement);
                Assert.True(rig.Manager.BeginIdlePoll());
                var resumed = await rig.ReceivePackets();
                AssertNeutralDsuPackets(resumed);
                Assert.True(BinaryPrimitives.ReadInt64LittleEndian(resumed[0].AsSpan(68)) > 0);
            }
            finally
            {
                rig.Manager.DsuServer = null;
            }
        }

        private sealed class IdlePolicyRig : IDisposable
        {
            private readonly Action _afterRefresh = SettingsService.AfterMappingSetsRefreshed;
            private readonly Dictionary<VirtualControllerType, int[]> _orders = new();
            private readonly InputService _service;
            private readonly Action _refresh;
            public DeviceService Devices { get; }

            public IdlePolicyRig(InputManager manager)
            {
                // These service methods do not need a running WPF application.
                // An application would also enable SettingsService's autosave timer.
                Assert.Null(System.Windows.Application.Current);
                foreach (var type in Enum.GetValues<VirtualControllerType>())
                {
                    var order = SettingsManager.SlotOrders.GetOrderFor(type);
                    _orders[type] = order.ToArray();
                    order.Clear();
                }
                try
                {
                    for (int i = 0; i < InputManager.MaxPads; i++)
                        if (SettingsManager.SlotCreated[i])
                            SettingsManager.SlotOrders.Add(i, manager.SlotControllerTypes[i]);
                    var vm = new PadForge.ViewModels.MainViewModel();
                    Devices = new DeviceService(vm, new SettingsService(vm));
                    // UpdateIdleState uses the manager and shared settings. Avoid
                    // the constructor's unrelated device and UI subscriptions.
                    _service = (InputService)System.Runtime.CompilerServices.RuntimeHelpers
                        .GetUninitializedObject(typeof(InputService));
                    typeof(InputService).GetField("_inputManager", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(_service, manager);
                    _refresh = typeof(InputService).GetMethod("UpdateIdleState",
                        BindingFlags.Instance | BindingFlags.NonPublic).CreateDelegate<Action>(_service);
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Refresh()
            {
                // Fixtures contain no NFC device. Keep its unrelated hint latch
                // at the current capture value so this call performs no native I/O.
                typeof(InputService).GetField("_switchNfcArmed", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(_service, NfcTagRegistry.RegistrationCaptureActive);
                _refresh();
            }

            public void Dispose()
            {
                SettingsService.AfterMappingSetsRefreshed = _afterRefresh;
                foreach (var pair in _orders)
                {
                    var order = SettingsManager.SlotOrders.GetOrderFor(pair.Key);
                    order.Clear();
                    order.AddRange(pair.Value);
                }
            }
        }

        private sealed class IdleController : IVirtualController
        {
            public VirtualControllerType Type => VirtualControllerType.Xbox;
            public bool IsConnected => true;
            public int FeedbackPadIndex { get; set; }
            public void Connect() { }
            public void Disconnect() { }
            public void SubmitGamepadState(Gamepad state) { }
            public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates) { }
            public void Dispose() { }
        }
        [Fact]
        public void ServerOff_SkipsAutomaticMotionAndResumesFromCurrentSamples()
        {
            using var rig = new MotionRig();
            var device = rig.AddDevice(0, 0.1f, 0f, 0f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, device);
            var server = rig.Manager.DsuServer;
            rig.Update();
            Assert.True(rig.Manager.DsuMotionSnapshots[0].HasMotion);
            rig.Manager.DsuServer = null;
            rig.Update();
            Assert.False(rig.Manager.DsuMotionSnapshots[0].HasMotion);
            device.InputState.Gyro[0] = 0.7f;
            rig.Manager.DsuServer = server;
            rig.Update();
            Assert.Equal(0.7f * 180f / MathF.PI, rig.Manager.DsuMotionSnapshots[0].GyroPitch, 4);
        }

        [Fact]
        public void AutomaticSnapshot_DoesNotAllocatePerPoll()
        {
            using var rig = new MotionRig();
            var device = rig.AddDevice(0, 0.1f, 0.2f, 0.3f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, device);
            for (int i = 0; i < 256; i++) rig.Update();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) rig.Update();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
        }

        [Fact]
        public async Task AutomaticSource_AfterSixtyFourAssignmentsStillReachesTheWire()
        {
            using var rig = new MotionRig();
            for (int i = 0; i < 64; i++)
                rig.AddDevice(0, 0f, 0f, 0f, gyro: false, accel: false, online: false);
            var device = rig.AddDevice(0, 0.3f, 0.4f, 0.5f);
            rig.ConfigureSlot(0, VirtualControllerType.Xbox, device);
            var packets = await rig.TickAndReceive();
            Assert.Equal(2, packets[0][21]);
            Assert.Equal(2, packets[0][22]);
            Assert.Equal(0.3f * 180f / MathF.PI, ReadFloat(packets[0], 88), 4);
        }

        private static MappingSource Source(UserDevice device, string descriptor, bool invert = false)
            => new() { Kind = "Direct", DeviceGuid = device.InstanceGuidString, Descriptor = descriptor, Invert = invert };

        private sealed class MotionDevice : WebControllerDevice, ISdlInputDevice
        {
            public MotionDevice() : base(Guid.NewGuid().ToString(), "Motion Test Device") { }
            public bool HasGyroAux { get; set; }
            public bool HasAccelAux { get; set; }
        }

        private static float ReadFloat(byte[] packet, int offset)
            => BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset, 4));

        private sealed class MotionRig : IDisposable
        {
            private readonly SettingsCollection _settings = SettingsManager.UserSettings;
            private readonly DeviceCollection _devices = SettingsManager.UserDevices;
            private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
            private readonly bool[] _created = SettingsManager.SlotCreated;
            private readonly bool[] _enabled = SettingsManager.SlotEnabled;
            private readonly Func<string, int, SourceCoercion.GyroTuning> _tuning = SourceCoercion.GyroTuningProvider;
            private readonly Func<string, int, (float, float, float)> _bias = SourceCoercion.GyroBiasProvider;
            private readonly DsuMotionServer _server = new();
            private readonly UdpClient _client = new(new IPEndPoint(IPAddress.Loopback, 0));
            private readonly List<WebControllerDevice> _wrappers = new();
            private readonly Action _update;
            private readonly Action _broadcast;
            public InputManager Manager { get; } = new();

            public MotionRig()
            {
                try
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
                    _update = typeof(InputManager).GetMethod("UpdateMotionSnapshots",
                        BindingFlags.Instance | BindingFlags.NonPublic).CreateDelegate<Action>(Manager);
                    _broadcast = typeof(InputManager).GetMethod("BroadcastDsuMotion",
                        BindingFlags.Instance | BindingFlags.NonPublic).CreateDelegate<Action>(Manager);
                    Assert.True(_server.Start(0));
                    Manager.DsuServer = _server;

                    // Register through the real subscription handler. Requests
                    // are not under test here. Replies travel over a real socket.
                    typeof(DsuMotionServer).GetMethod("HandlePadDataRequest",
                        BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_server,
                        new object[] { new byte[28], 28, _client.Client.LocalEndPoint });
                    }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public UserDevice AddDevice(int slot, float x, float y, float z,
                bool gyro = true, bool accel = true, bool online = true)
            {
                var wrapper = new MotionDevice
                {
                    HasGyro = gyro, HasAccel = accel,
                };
                _wrappers.Add(wrapper);
                var state = new CustomInputState();
                state.Gyro[0] = x;
                state.Gyro[1] = y;
                state.Gyro[2] = z;
                state.Accel[0] = 0.25f * 9.80665f;
                state.Accel[1] = 0.5f * 9.80665f;
                state.Accel[2] = 0.75f * 9.80665f;
                var device = new UserDevice
                {
                    InstanceGuid = Guid.NewGuid(), Device = wrapper, InputState = state,
                    IsOnline = online, HasGyro = gyro, HasAccel = accel,
                    CapType = InputDeviceType.Gamepad,
                };
                SettingsManager.UserDevices.Items.Add(device);
                Assign(device, slot);
                return device;
            }

            public void Assign(UserDevice device, int slot)
                => SettingsManager.UserSettings.Items.Add(new UserSetting
                {
                    InstanceGuid = device.InstanceGuid, MapTo = slot,
                });

            public void ConfigureSlot(int slot, VirtualControllerType type, UserDevice device)
            {
                SettingsManager.SlotCreated[slot] = true;
                SettingsManager.SlotEnabled[slot] = true;
                Manager.SlotControllerTypes[slot] = type;
                var set = new MappingSet();
                MappingSetMigrator.EnsureMotionRows(set, (int)type,
                    new[] { (device.InstanceGuidString, device.HasGyro, device.HasAccel) });
                SettingsManager.SlotMappingSets[slot] = set;
            }

            public async Task<Dictionary<int, byte[]>> TickAndReceive()
            {
                _update();
                _broadcast();
                return await ReceivePackets();
            }

            public async Task<Dictionary<int, byte[]>> ReceivePackets()
            {
                var packets = new Dictionary<int, byte[]>();
                while (packets.Count < 4)
                {
                    var result = await _client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    Assert.Equal(100, result.Buffer.Length);
                    packets.Add(result.Buffer[20], result.Buffer);
                }
                return packets;
            }

            public int AvailablePacketBytes => _client.Available;

            public void UseServer(DsuMotionServer server)
            {
                Manager.DsuServer = server;
                typeof(DsuMotionServer).GetMethod("HandlePadDataRequest",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(server,
                    new object[] { new byte[28], 28, _client.Client.LocalEndPoint });
            }

            public void Update() => _update();

            public void Dispose()
            {
                try
                {
                    Manager.DsuServer = null;
                    _server.Dispose();
                    _client.Dispose();
                    Manager.Dispose();
                    foreach (var wrapper in _wrappers) wrapper.Dispose();
                    }
                finally
                {
                    SettingsManager.UserSettings = _settings;
                    SettingsManager.UserDevices = _devices;
                    SettingsManager.SlotMappingSets = _sets;
                    SettingsManager.SlotCreated = _created;
                    SettingsManager.SlotEnabled = _enabled;
                    SourceCoercion.GyroTuningProvider = _tuning;
                    SourceCoercion.GyroBiasProvider = _bias;
                    }
            }
        }
    }
}
