using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Motion rows follow shift layers (owner decision, 2026-07-26).
    ///
    /// <para>The engine resolve now prefers the engaged layer's motion row,
    /// falls back to Base, and finally to any row naming the target, so the
    /// change is a strict preference re-ordering and motion can never go dark
    /// because a layer engaged.</para>
    ///
    /// <para>These lock the half of that change with a pure-data seam: the
    /// load-time backfill. It used to find the motion row LAYER-BLIND, so on a
    /// slot that already had a shift-layer motion row it appended the newly
    /// assigned device's sources there and never created a Base row. With the
    /// resolve now layer-aware that would have cost the slot its motion
    /// everywhere except inside that one layer.</para>
    ///
    /// <para>The runtime half runs the real motion pass (DC34): a motion row
    /// the user emptied, or a Do Not Inherit row with no source, switches
    /// its channel off with no fallthrough, while a layer with no motion row
    /// of its own keeps Base motion and an offline row still hands off to a
    /// row with a live device.</para></summary>
    [Collection("SettingsManagerStatics")]
    public class MotionLayerTests
    {
        private const string GyroDev = "11111111-1111-1111-1111-111111111111";

        private static MappingSet SetWithLayerMotionRow()
        {
            var ms = new MappingSet();
            // A motion row that exists ONLY on a shift layer, placed first so a
            // layer-blind find would take it.
            ms.Rows.Add(new MappingRow
            {
                Target = MappingSetMigrator.MotionGyroTarget,
                LayerMask = "Shift1",
                Sources = new List<MappingSource>(),
            });
            return ms;
        }

        private static IReadOnlyList<(string DeviceGuid, bool HasGyro, bool HasAccel)> OneGyroPad()
            => new[] { (GyroDev, true, true) };

        /// <summary>THE TRAP. A pre-existing shift-layer motion row must not
        /// capture the backfill; the slot still needs its Base row.</summary>
        [Fact]
        public void BackfillCreatesTheBaseRowEvenWhenALayerRowExistsFirst()
        {
            var ms = SetWithLayerMotionRow();

            // slotType 1 = PlayStation, one of the two families that get motion rows.
            MappingSetMigrator.EnsureMotionRows(ms, 1, OneGyroPad());

            var gyroRows = ms.Rows
                .Where(r => r.Target == MappingSetMigrator.MotionGyroTarget)
                .ToList();

            Assert.Contains(gyroRows, r => (r.LayerMask ?? "Base") == "Base");

            var baseRow = gyroRows.First(r => (r.LayerMask ?? "Base") == "Base");
            Assert.NotNull(baseRow.Sources);
            Assert.Contains(baseRow.Sources, s => s.DeviceGuid == GyroDev);
        }

        /// <summary>And the layer row must be left alone: the backfill is not
        /// entitled to stuff the slot's devices into someone's shift layer.</summary>
        [Fact]
        public void BackfillDoesNotWriteIntoTheLayerRow()
        {
            var ms = SetWithLayerMotionRow();

            MappingSetMigrator.EnsureMotionRows(ms, 1, OneGyroPad());

            var layerRow = ms.Rows.First(r =>
                r.Target == MappingSetMigrator.MotionGyroTarget && r.LayerMask == "Shift1");
            Assert.Empty(layerRow.Sources);
        }

        /// <summary>A null LayerMask means Base. Hand-edited XML and imported
        /// profiles deliver it, and treating it as a layer would make the
        /// backfill create a duplicate Base row beside the real one.</summary>
        [Fact]
        public void NullLayerMaskCountsAsBase()
        {
            var ms = new MappingSet();
            ms.Rows.Add(new MappingRow
            {
                Target = MappingSetMigrator.MotionGyroTarget,
                LayerMask = null,
                Sources = new List<MappingSource>(),
            });

            MappingSetMigrator.EnsureMotionRows(ms, 1, OneGyroPad());

            var gyroRows = ms.Rows
                .Where(r => r.Target == MappingSetMigrator.MotionGyroTarget)
                .ToList();
            Assert.Single(gyroRows);
            Assert.Empty(gyroRows[0].Sources);
            var accelRow = Assert.Single(ms.Rows, r => r.Target == MappingSetMigrator.MotionAccelTarget);
            Assert.Contains(accelRow.Sources, s => s.DeviceGuid == GyroDev);
        }

        /// <summary>The ordinary case still works: no rows at all yields a
        /// Base row carrying the device.</summary>
        [Fact]
        public void BackfillFromEmptyCreatesBase()
        {
            var ms = new MappingSet();

            MappingSetMigrator.EnsureMotionRows(ms, 1, OneGyroPad());

            var gyroRow = ms.Rows.FirstOrDefault(r => r.Target == MappingSetMigrator.MotionGyroTarget);
            Assert.NotNull(gyroRow);
            Assert.Equal("Base", gyroRow.LayerMask ?? "Base");
            Assert.Contains(gyroRow.Sources, s => s.DeviceGuid == GyroDev);
        }

        // ── DC34: the motion pass, run through the real engine ──

        private const float RadToDeg = 180f / MathF.PI;

        private sealed class GyroDevice : WebControllerDevice, ISdlInputDevice
        {
            public GyroDevice() : base(Guid.NewGuid().ToString(), "Gyro Test Device") { }
            public bool HasGyroAux { get; set; }
            public bool HasAccelAux { get; set; }
        }

        /// <summary>One PlayStation slot with Motion Gyro rows only, so the
        /// accelerometer channel stays out of the snapshot. Gyro rates go in
        /// as radians a second and come out of the snapshot in degrees.</summary>
        private sealed class Rig : IDisposable
        {
            private readonly SettingsCollection _settings = SettingsManager.UserSettings;
            private readonly DeviceCollection _devices = SettingsManager.UserDevices;
            private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
            private readonly bool[] _created = SettingsManager.SlotCreated;
            private readonly bool[] _enabled = SettingsManager.SlotEnabled;
            private readonly Func<string, int, SourceCoercion.GyroTuning> _tuning = SourceCoercion.GyroTuningProvider;
            private readonly Func<string, int, (float, float, float)> _bias = SourceCoercion.GyroBiasProvider;
            private readonly List<GyroDevice> _wrappers = new();
            private readonly MethodInfo _update = typeof(InputManager).GetMethod(
                "UpdateMotionSnapshots", BindingFlags.Instance | BindingFlags.NonPublic);

            public InputManager Manager { get; } = new();
            public MappingSet Set { get; } = new();

            public Rig()
            {
                InputManager.ClearAllShiftRuntime();
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
                SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
                SettingsManager.SlotEnabled = new bool[InputManager.MaxPads];
                SettingsManager.SlotCreated[0] = true;
                SettingsManager.SlotEnabled[0] = true;
                SettingsManager.SlotMappingSets[0] = Set;
                Manager.SlotControllerTypes[0] = VirtualControllerType.PlayStation;
                SourceCoercion.GyroTuningProvider = (_, _) => new SourceCoercion.GyroTuning
                {
                    Grip = "Pointing", ApplyToPassthrough = false,
                };
                SourceCoercion.GyroBiasProvider = (_, _) => (0f, 0f, 0f);
            }

            public UserDevice AddPad(float pitch, bool online = true)
            {
                var wrapper = new GyroDevice { HasGyro = true, HasAccel = true };
                _wrappers.Add(wrapper);
                var state = new CustomInputState();
                state.Gyro[0] = pitch;
                var device = new UserDevice
                {
                    InstanceGuid = Guid.NewGuid(), Device = wrapper, InputState = state,
                    IsOnline = online, HasGyro = true, HasAccel = true,
                    CapType = InputDeviceType.Gamepad,
                };
                SettingsManager.UserDevices.Items.Add(device);
                SettingsManager.UserSettings.Items.Add(new UserSetting
                {
                    InstanceGuid = device.InstanceGuid, MapTo = 0,
                });
                return device;
            }

            public MappingRow GyroRow(string layer, params UserDevice[] devices)
            {
                var row = new MappingRow
                {
                    Target = MappingSetMigrator.MotionGyroTarget,
                    LayerMask = layer,
                    Sources = devices.Select(d => new MappingSource
                    {
                        Kind = "Direct", DeviceGuid = d.InstanceGuidString,
                        Descriptor = MappingSetMigrator.MotionGyroSourceDescriptor,
                    }).ToList(),
                };
                Set.Rows.Add(row);
                return row;
            }

            public void Layer(string mask, bool inheritUnmapped)
                => Set.ShiftActivators.Add(new ShiftActivator
                {
                    DeviceGuid = "", Descriptor = "Button 28", Mode = "Hold",
                    LayerMask = mask, LayerName = mask, Kind = "Button",
                    InheritUnmapped = inheritUnmapped, DelayMs = 0, AutoCancelMs = 0,
                });

            public void Engage(string mask) => InputManager.ApplyMacroLayerSwitch(0, mask);

            /// <summary>One motion pass. NaN when the slot sends no motion.</summary>
            public float Pitch()
            {
                _update.Invoke(Manager, null);
                var s = Manager.MotionSnapshots[0];
                return s.HasMotion ? s.GyroPitch : float.NaN;
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
                    InputManager.ClearAllShiftRuntime();
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

        /// <summary>An emptied Base row is Base's motion switched off. While
        /// Base is active nothing may fill it, a live row on a layer that is
        /// not engaged included. It went dark only when no other row
        /// existed.</summary>
        [Fact]
        public void EmptiedBaseRow_WhileBaseIsActive_SendsNothing_EvenBesideALiveLayerRow()
        {
            using var rig = new Rig();
            var pad = rig.AddPad(0.4f);
            rig.Layer("View", inheritUnmapped: false);
            rig.GyroRow("Base");
            rig.GyroRow("View", pad);

            Assert.True(float.IsNaN(rig.Pitch()), "an inactive layer's row filled Base's switched-off motion");
        }

        /// <summary>The engaged replace layer's emptied row switches motion
        /// off on that layer. It fell back to Base.</summary>
        [Fact]
        public void EmptiedRowOnTheEngagedReplaceLayer_SendsNothing()
        {
            using var rig = new Rig();
            var pad = rig.AddPad(0.2f);
            rig.Layer("View", inheritUnmapped: false);
            rig.GyroRow("Base", pad);
            rig.GyroRow("View");
            rig.Engage("View");

            Assert.True(float.IsNaN(rig.Pitch()), "the engaged layer's emptied motion row fell back to Base");
        }

        /// <summary>The same on an overlay layer, which lets Base fall
        /// through only for targets the layer has no row for.</summary>
        [Fact]
        public void EmptiedRowOnTheEngagedOverlayLayer_SendsNothing()
        {
            using var rig = new Rig();
            var pad = rig.AddPad(0.2f);
            rig.Layer("View", inheritUnmapped: true);
            rig.GyroRow("Base", pad);
            rig.GyroRow("View");
            rig.Engage("View");

            Assert.True(float.IsNaN(rig.Pitch()), "the engaged overlay layer's emptied motion row fell back to Base");
        }

        /// <summary>Do Not Inherit keeps a target off on its layer. A button
        /// row already did, and a motion row did not.</summary>
        [Fact]
        public void DoNotInheritRowOnTheEngagedLayer_SendsNothing()
        {
            using var rig = new Rig();
            var pad = rig.AddPad(0.2f);
            rig.Layer("View", inheritUnmapped: true);
            rig.GyroRow("Base", pad);
            rig.GyroRow("View").NoInherit = true;
            rig.Engage("View");

            Assert.True(float.IsNaN(rig.Pitch()), "a Do Not Inherit motion row on the engaged layer did not mute");
        }

        /// <summary>A layer with no motion row of its own leaves Base's row
        /// applying, so when Base's row is switched off the channel stays
        /// off, with no fallthrough to another layer's row.</summary>
        [Fact]
        public void EmptiedBaseRow_UnderALayerWithoutItsOwnRow_SendsNothing()
        {
            using var rig = new Rig();
            var pad = rig.AddPad(0.4f);
            rig.Layer("View", inheritUnmapped: false);
            rig.Layer("Other", inheritUnmapped: false);
            rig.GyroRow("Base");
            rig.GyroRow("Other", pad);
            rig.Engage("View");

            Assert.True(float.IsNaN(rig.Pitch()), "another layer's row filled Base's switched-off motion");
        }

        /// <summary>Control, the 2026-07-26 decision: a replace layer with no
        /// motion row of its own does not silence motion.</summary>
        [Fact]
        public void AReplaceLayerWithNoMotionRow_KeepsBaseMotion()
        {
            using var rig = new Rig();
            var pad = rig.AddPad(0.2f);
            rig.Layer("View", inheritUnmapped: false);
            rig.GyroRow("Base", pad);
            rig.Engage("View");

            Assert.Equal(0.2f * RadToDeg, rig.Pitch(), 3);
        }

        /// <summary>Control, the recorded hand-off: a row whose devices are
        /// offline hands off to a row with a live device, from Base to
        /// another layer's row and from the engaged layer's row to Base.</summary>
        [Fact]
        public void AnOfflineRow_StillHandsOffToARowWithALiveDevice()
        {
            using (var rig = new Rig())
            {
                var offline = rig.AddPad(9f, online: false);
                var live = rig.AddPad(0.4f);
                rig.Layer("View", inheritUnmapped: false);
                rig.GyroRow("Base", offline);
                rig.GyroRow("View", live);
                Assert.Equal(0.4f * RadToDeg, rig.Pitch(), 3);
            }
            using (var rig = new Rig())
            {
                var offline = rig.AddPad(9f, online: false);
                var live = rig.AddPad(0.2f);
                rig.Layer("View", inheritUnmapped: false);
                rig.GyroRow("Base", live);
                rig.GyroRow("View", offline);
                rig.Engage("View");
                Assert.Equal(0.2f * RadToDeg, rig.Pitch(), 3);
            }
        }

        /// <summary>Control: the engaged layer's own live row drives.</summary>
        [Fact]
        public void TheEngagedLayersOwnLiveRow_Drives()
        {
            using var rig = new Rig();
            var basePad = rig.AddPad(0.2f);
            var layerPad = rig.AddPad(0.4f);
            rig.Layer("View", inheritUnmapped: false);
            rig.GyroRow("Base", basePad);
            rig.GyroRow("View", layerPad);
            rig.Engage("View");

            Assert.Equal(0.4f * RadToDeg, rig.Pitch(), 3);
        }

        /// <summary>False-positive guard: only a row with no motion input
        /// switches its channel off. These three shapes on the engaged
        /// layer's row each carry live motion and must drive it, not go dark
        /// and not fall back to Base's 0.2: an (Any Device) source, a Custom
        /// row whose first argument is the empty place a removed device left,
        /// and a live source beside an Invert on Hold modifier that is not
        /// held.</summary>
        [Theory]
        [InlineData("AnyDevice")]
        [InlineData("CustomWithAnEmptyPlace")]
        [InlineData("BesideAnInvertModifier")]
        public void ALayerRowWithLiveMotion_IsNotTreatedAsEmpty(string shape)
        {
            using var rig = new Rig();
            var basePad = rig.AddPad(0.2f);
            var layerPad = rig.AddPad(0.4f);
            rig.Layer("View", inheritUnmapped: false);
            rig.GyroRow("Base", basePad);
            var row = rig.GyroRow("View");
            var live = new MappingSource
            {
                Kind = "Direct", DeviceGuid = layerPad.InstanceGuidString,
                Descriptor = MappingSetMigrator.MotionGyroSourceDescriptor,
            };
            switch (shape)
            {
                case "AnyDevice":
                    live.DeviceGuid = "";
                    row.Sources.Add(live);
                    break;
                case "CustomWithAnEmptyPlace":
                    row.CombineMode = "Custom";
                    row.CombineExpression = "a + b";
                    row.Sources.Add(new MappingSource());
                    row.Sources.Add(live);
                    break;
                case "BesideAnInvertModifier":
                    row.Sources.Add(new MappingSource { Kind = "InvertOnHold", ParamModifier = "Button 5" });
                    row.Sources.Add(live);
                    break;
            }
            rig.Engage("View");

            Assert.Equal(0.4f * RadToDeg, rig.Pitch(), 3);
        }
    }
}
