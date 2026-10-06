using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Data;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Motion Pitch, Yaw and Roll rows (#475) on the Pad page: which
    /// grids carry them, their settings, the notes, the picker lists the
    /// Motion rows read from, and the recorder's motion branch.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MotionRowsUiTests
    {
        private static readonly string[] AxisTargets =
        {
            MappingSetMigrator.MotionPitchTarget,
            MappingSetMigrator.MotionYawTarget,
            MappingSetMigrator.MotionRollTarget,
        };

        private static readonly string[] MotionTargets =
        {
            MappingSetMigrator.MotionGyroTarget,
            MappingSetMigrator.MotionAccelTarget,
            MappingSetMigrator.MotionPitchTarget,
            MappingSetMigrator.MotionYawTarget,
            MappingSetMigrator.MotionRollTarget,
        };

        private static PadViewModel Grid(VirtualControllerType type, string profile)
            => RestoredPad.Build(0, type, profile);

        private static MappingItem Row(string target)
            => new(target, target, MappingCategory.Motion,
                MappingSetMigrator.IsMotionAxisTarget(target) ? target + "Neg" : null, includeInMapAll: false);

        // ── The grids ──

        [Theory]
        [InlineData(VirtualControllerType.PlayStation, null)]
        [InlineData(VirtualControllerType.PlayStation, "dualshock-4-v1")]
        [InlineData(VirtualControllerType.Nintendo, null)]
        [InlineData(VirtualControllerType.Nintendo, "switch2-pro-controller")]
        [InlineData(VirtualControllerType.Nintendo, "switch2-pro-controller-composite")]
        [InlineData(VirtualControllerType.Extended, "steam-deck-composite")]
        [InlineData(VirtualControllerType.Extended, "steam-controller")]
        [InlineData(VirtualControllerType.Extended, "steam-controller-2")]
        public void EveryMotionGridEndsWithTheFiveMotionRows(VirtualControllerType type, string profile)
        {
            var vm = Grid(type, profile);
            var tail = vm.Mappings.Skip(vm.Mappings.Count - MotionTargets.Length).ToList();
            Assert.Equal(MotionTargets, tail.Select(m => m.TargetSettingName));
            Assert.Equal(MotionTargets.Length, vm.Mappings.Count(m => m.Category == MappingCategory.Motion));
            foreach (var m in tail)
            {
                Assert.Equal(MappingCategory.Motion, m.Category);
                // Map All recorded sticks onto the Motion Gyro row (#473).
                Assert.False(m.IncludeInMapAll, $"{m.TargetSettingName} is in the Map All walk");
            }
            foreach (var t in AxisTargets)
            {
                var m = tail.Single(r => r.TargetSettingName == t);
                Assert.Equal(t + "Neg", m.NegSettingName);
                Assert.True(m.IsMotionAxisRow);
                Assert.True(m.IsRecordable);
            }
        }

        [Theory]
        [InlineData(VirtualControllerType.Xbox, null)]
        [InlineData(VirtualControllerType.Extended, null)]
        [InlineData(VirtualControllerType.Extended, "switch-pro")]
        public void AGridWithoutMotionHasNoMotionRows(VirtualControllerType type, string profile)
        {
            var vm = Grid(type, profile);
            Assert.DoesNotContain(vm.Mappings, m => MotionTargets.Contains(m.TargetSettingName));
        }

        [Theory]
        [InlineData(VirtualControllerType.PlayStation, "dualshock-3", true)]
        [InlineData(VirtualControllerType.Nintendo, "switch2-pro-controller", true)]
        [InlineData(VirtualControllerType.Nintendo, "switch2-pro-controller-composite", false)]
        [InlineData(VirtualControllerType.PlayStation, "dualsense-composite", false)]
        [InlineData(VirtualControllerType.PlayStation, "dualshock-4-v1", false)]
        [InlineData(VirtualControllerType.Nintendo, "switch-pro", false)]
        [InlineData(VirtualControllerType.Extended, "steam-deck-composite", false)]
        [InlineData(VirtualControllerType.Extended, "steam-deck", true)]
        public void APresetWithoutMotionNotesItOnTheMotionRows(VirtualControllerType type, string profile, bool noMotion)
        {
            var vm = Grid(type, profile);
            foreach (var m in vm.Mappings)
            {
                bool motion = MotionTargets.Contains(m.TargetSettingName);
                Assert.Equal(motion && noMotion, m.PresetCarriesNoMotion);
                if (!motion) continue;
                Assert.Equal(noMotion ? Strings.Instance.Pad_Mapping_MotionPresetNote : null, m.MotionRowNote);
                Assert.Equal(noMotion, m.ShowMotionRowNote);
            }
        }

        /// <summary>The DualShock 3 (SIXAXIS): Full's report carries the
        /// accelerometer and the yaw rate but no pitch or roll rate, so a
        /// Speed turn on Motion Pitch or Roll reaches the game only as tilt.
        /// The note sits on those two rows in Speed mode and leaves with
        /// Angle mode, which leans the accelerometer the report carries.</summary>
        [Fact]
        public void TheFullPresetNotesASpeedPitchOrRollTurn()
        {
            var vm = Grid(VirtualControllerType.PlayStation, "dualshock-3-full");
            MappingItem RowOf(string target) => vm.Mappings.Single(m => m.TargetSettingName == target);
            string note = Strings.Instance.Pad_Mapping_MotionPitchRollRateNote;
            var pitch = RowOf(MappingSetMigrator.MotionPitchTarget);
            Assert.Equal(note, pitch.MotionRowNote);
            Assert.Equal(note, RowOf(MappingSetMigrator.MotionRollTarget).MotionRowNote);
            Assert.Null(RowOf(MappingSetMigrator.MotionYawTarget).MotionRowNote);
            Assert.Null(RowOf("MotionGyro").MotionRowNote);

            var raised = new List<string>();
            pitch.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            pitch.MotionResponse = MappingRow.MotionResponseAngle;
            Assert.Null(pitch.MotionRowNote);
            Assert.Contains(nameof(MappingItem.MotionRowNote), raised);

            Assert.All(Grid(VirtualControllerType.PlayStation, "dualsense-composite").Mappings,
                m => Assert.False(m.PresetCarriesNoPitchRollRate));
        }

        [Fact]
        public void SwitchingPresetsMovesTheNote()
        {
            var vm = Grid(VirtualControllerType.PlayStation, "dualshock-4-v1");
            Assert.All(vm.Mappings, m => Assert.False(m.PresetCarriesNoMotion));
            vm.ProfileId = "dualshock-3";
            Assert.True(vm.Mappings.Single(m => m.TargetSettingName == MappingSetMigrator.MotionYawTarget).PresetCarriesNoMotion);
            vm.ProfileId = "dualsense-composite";
            Assert.All(vm.Mappings, m => Assert.False(m.PresetCarriesNoMotion));
        }

        // ── The rows ──

        [Fact]
        public void TheRowsTargetBipolarAxes()
        {
            foreach (var t in AxisTargets)
            {
                Assert.Equal(TargetKind.BipolarAxis, TargetKindResolver.Resolve(t));
                var m = Row(t);
                Assert.True(m.IsBipolarAxisTarget);
                Assert.False(m.IsTargetDiscrete);
                m.LoadDescriptor("Axis 1");
                Assert.False(m.IsDeadZoneApplicable, $"{t} offered a button's deadzone to a stick");
                m.AddExtraSourceCommand.Execute(null);
                Assert.Equal("MaxAbs", m.CombineMode);
            }
            // The passthrough rows keep their own rules.
            Assert.False(MappingSetMigrator.IsMotionAxisTarget(MappingSetMigrator.MotionGyroTarget));
            Assert.False(MappingSetMigrator.IsMotionTarget(MappingSetMigrator.MotionPitchTarget));
        }

        [Fact]
        public void PitchAndRollLeanAndYawOnlyTurns()
        {
            var pitch = Row(MappingSetMigrator.MotionPitchTarget);
            var yaw = Row(MappingSetMigrator.MotionYawTarget);
            var roll = Row(MappingSetMigrator.MotionRollTarget);
            Assert.True(pitch.CanUseMotionAngle);
            Assert.True(roll.CanUseMotionAngle);
            Assert.False(yaw.CanUseMotionAngle);

            foreach (var m in new[] { pitch, yaw, roll })
            {
                Assert.True(m.ShowMotionSpeedSettings);
                Assert.False(m.ShowMotionAngleSettings);
                m.MotionResponse = MappingRow.MotionResponseAngle;
            }
            Assert.True(pitch.IsMotionAngle);
            Assert.True(pitch.ShowMotionAngleSettings);
            Assert.False(pitch.ShowMotionSpeedSettings);
            // Yaw stores the value and still turns at a speed.
            Assert.Equal(MappingRow.MotionResponseAngle, yaw.MotionResponse);
            Assert.False(yaw.IsMotionAngle);
            Assert.True(yaw.ShowMotionSpeedSettings);
            Assert.False(yaw.ShowMotionAngleSettings);

            // Any other row shows neither panel.
            var button = new MappingItem("A", "ButtonA", MappingCategory.Buttons) { MotionResponse = MappingRow.MotionResponseAngle };
            Assert.False(button.IsMotionAxisRow);
            Assert.False(button.ShowMotionSpeedSettings);
            Assert.False(button.ShowMotionAngleSettings);
        }

        [Theory]
        [InlineData("Angle", "Angle")]
        [InlineData("", "")]
        [InlineData("angle", "")]
        [InlineData("Speed", "")]
        [InlineData(null, "")]
        public void TheResponseReadsAsSpeedUnlessItIsAngle(string stored, string expected)
        {
            var m = Row(MappingSetMigrator.MotionRollTarget);
            m.MotionResponse = stored;
            Assert.Equal(expected, m.MotionResponse);
        }

        [Fact]
        public void TheResponseRaisesThePanelsItMoves()
        {
            var m = Row(MappingSetMigrator.MotionPitchTarget);
            var raised = new List<string>();
            m.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            m.MotionResponse = MappingRow.MotionResponseAngle;
            Assert.Contains(nameof(MappingItem.MotionResponse), raised);
            Assert.Contains(nameof(MappingItem.IsMotionAngle), raised);
            Assert.Contains(nameof(MappingItem.ShowMotionSpeedSettings), raised);
            Assert.Contains(nameof(MappingItem.ShowMotionAngleSettings), raised);
        }

        [Fact]
        public void TheSettingsStayInTheirRanges()
        {
            var m = Row(MappingSetMigrator.MotionPitchTarget);
            m.MotionSpeed = 0; Assert.Equal(1, m.MotionSpeed);
            m.MotionSpeed = 5000; Assert.Equal(MappingRow.MaxMotionSpeed, m.MotionSpeed);
            m.MotionMinSpeed = -5; Assert.Equal(0, m.MotionMinSpeed);
            m.MotionMinSpeed = 5000; Assert.Equal(MappingRow.MaxMotionSpeed, m.MotionMinSpeed);
            m.MotionAngle = 0; Assert.Equal(1, m.MotionAngle);
            m.MotionAngle = 200; Assert.Equal(MappingRow.MaxMotionAngle, m.MotionAngle);
            m.MotionDeadzone = -1; Assert.Equal(0, m.MotionDeadzone);
            m.MotionDeadzone = 95; Assert.Equal(MappingRow.MaxMotionDeadzone, m.MotionDeadzone);
        }

        [Fact]
        public void EachSettingResetsToItsDefault()
        {
            var m = Row(MappingSetMigrator.MotionRollTarget);
            Assert.Equal("", m.MotionResponse);
            Assert.Equal(MappingRow.DefaultMotionSpeed, m.MotionSpeed);
            Assert.Equal(0, m.MotionMinSpeed);
            Assert.Equal(MappingRow.DefaultMotionAngle, m.MotionAngle);
            Assert.Equal(MappingRow.DefaultMotionDeadzone, m.MotionDeadzone);

            m.MotionResponse = MappingRow.MotionResponseAngle;
            m.MotionSpeed = 900;
            m.MotionMinSpeed = 40;
            m.MotionAngle = 30;
            m.MotionDeadzone = 5;
            foreach (var name in new[]
                     {
                         nameof(MappingItem.MotionResponse), nameof(MappingItem.MotionSpeed),
                         nameof(MappingItem.MotionMinSpeed), nameof(MappingItem.MotionAngle),
                         nameof(MappingItem.MotionDeadzone),
                     })
            {
                Assert.True(m.ResetSettingCommand.CanExecute(name), name);
                m.ResetSettingCommand.Execute(name);
            }
            Assert.Equal("", m.MotionResponse);
            Assert.Equal(MappingRow.DefaultMotionSpeed, m.MotionSpeed);
            Assert.Equal(0, m.MotionMinSpeed);
            Assert.Equal(MappingRow.DefaultMotionAngle, m.MotionAngle);
            Assert.Equal(MappingRow.DefaultMotionDeadzone, m.MotionDeadzone);
        }

        // ── The notes ──

        [Theory]
        [InlineData("MotionGyro")]
        [InlineData("MotionAccel")]
        public void AMotionRowHoldingAStickSaysItReadsNothing(string target)
        {
            var m = Row(target);
            Assert.Null(m.MotionRowNote);
            var primary = new List<string>();
            m.PropertyChanged += (_, e) => primary.Add(e.PropertyName);
            m.LoadDescriptor("Axis 0");
            Assert.Equal(Strings.Instance.Pad_Mapping_MotionRowUnreadNote, m.MotionRowNote);
            Assert.Contains(nameof(MappingItem.MotionRowNote), primary);
            Assert.True(m.ShowMotionRowNote);

            m.LoadDescriptor(target == "MotionGyro" ? "Motion Gyro" : "Motion Accel");
            Assert.Null(m.MotionRowNote);

            // A modifier is a button by design.
            var modifier = new MappingSourceItem { Kind = "InvertOnHold", Descriptor = "Button 0", ParamModifier = "Button 0" };
            m.ExtraSources.Add(modifier);
            Assert.Null(m.MotionRowNote);

            // A second source that is a stick is not.
            var raised = new List<string>();
            m.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            m.ExtraSources.Add(new MappingSourceItem { Descriptor = "Button 3" });
            Assert.Equal(Strings.Instance.Pad_Mapping_MotionRowUnreadNote, m.MotionRowNote);
            Assert.Contains(nameof(MappingItem.MotionRowNote), raised);
        }

        [Theory]
        [InlineData("ButtonA", "Motion Accel")]
        [InlineData("LeftThumbAxisX", "Motion Gyro")]
        [InlineData("MotionPitch", "Motion Gyro L")]
        [InlineData("MotionYaw", "Motion Accel L")]
        public void AMotionSourceOnAnotherRowSaysItReadsNothingThere(string target, string descriptor)
        {
            var m = new MappingItem(target, target, MappingCategory.Buttons);
            m.LoadDescriptor(descriptor);
            Assert.Equal(Strings.Instance.Pad_Mapping_MotionSourceElsewhereNote, m.MotionRowNote);

            // A single axis of the sensor reads on any row.
            m.LoadDescriptor("Gyro Pitch");
            Assert.Null(m.MotionRowNote);

            var raised = new List<string>();
            m.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            var extra = new MappingSourceItem { Descriptor = "Button 1" };
            m.ExtraSources.Add(extra);
            raised.Clear();
            extra.Descriptor = descriptor;
            Assert.Equal(Strings.Instance.Pad_Mapping_MotionSourceElsewhereNote, m.MotionRowNote);
            Assert.Contains(nameof(MappingItem.MotionRowNote), raised);
        }

        [Fact]
        public void TheMoreSpecificNoteWinsOnAPresetWithoutMotion()
        {
            var m = new MappingItem("Pitch", MappingSetMigrator.MotionPitchTarget, MappingCategory.Motion,
                MappingSetMigrator.MotionPitchTarget + "Neg", includeInMapAll: false) { PresetCarriesNoMotion = true };
            Assert.Equal(Strings.Instance.Pad_Mapping_MotionPresetNote, m.MotionRowNote);
            m.LoadDescriptor("Motion Gyro");
            Assert.Equal(Strings.Instance.Pad_Mapping_MotionSourceElsewhereNote, m.MotionRowNote);

            // Every other row on that preset stays quiet.
            var button = new MappingItem("A", "ButtonA", MappingCategory.Buttons) { PresetCarriesNoMotion = true };
            Assert.Null(button.MotionRowNote);
        }

        // ── The picker lists ──

        private static UserDevice MotionPad(Guid id) => new()
        {
            InstanceGuid = id, ProductGuid = id, IsOnline = true,
            ProductName = "Motion Pad", InstanceName = "Motion Pad",
            CapType = InputDeviceType.Gamepad, CapButtonCount = 4, CapAxeCount = 4,
            HasGyro = true, HasAccel = true, HasGyroAux = true, HasAccelAux = true,
            InputState = new CustomInputState(),
        };

        [Fact]
        public void TheMotionRowsPickFromTheirOwnLists()
        {
            var savedDevices = SettingsManager.UserDevices;
            var savedSettings = SettingsManager.UserSettings;
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                var id = Guid.NewGuid();
                var ud = MotionPad(id);
                SettingsManager.UserDevices.Items.Add(ud);
                var vm = new MainViewModel();
                var svc = new InputService(vm) { SettingsService = new SettingsService(vm) };
                var pad = vm.Pads[0];
                pad.OutputType = VirtualControllerType.PlayStation;
                typeof(InputService).GetMethod("PopulateAvailableInputs", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(svc, new object[] { pad, ud });

                string key = id.ToString().ToLowerInvariant();
                Assert.DoesNotContain(pad.SlotAvailableInputs, c => MappingSetMigrator.IsMotionVectorDescriptor(c.Descriptor));
                Assert.Contains(pad.SlotAvailableInputs, c => c.Descriptor == "Gyro Pitch" && c.DeviceGuid == key);
                Assert.Equal(new[] { MappingSetMigrator.MotionGyroSourceDescriptor, MappingSetMigrator.MotionGyroAuxSourceDescriptor },
                    pad.SlotMotionGyroInputs.Where(c => c.DeviceGuid == key).Select(c => c.Descriptor).OrderBy(d => d.Length));
                Assert.Equal(new[] { MappingSetMigrator.MotionAccelSourceDescriptor, MappingSetMigrator.MotionAccelAuxSourceDescriptor },
                    pad.SlotMotionAccelInputs.Where(c => c.DeviceGuid == key).Select(c => c.Descriptor).OrderBy(d => d.Length));

                var gyroRow = pad.Mappings.Single(m => m.TargetSettingName == MappingSetMigrator.MotionGyroTarget);
                var accelRow = pad.Mappings.Single(m => m.TargetSettingName == MappingSetMigrator.MotionAccelTarget);
                var yawRow = pad.Mappings.Single(m => m.TargetSettingName == MappingSetMigrator.MotionYawTarget);
                Assert.Same(pad.SlotMotionGyroInputs, gyroRow.AvailableInputs);
                Assert.Same(pad.SlotMotionAccelInputs, accelRow.AvailableInputs);
                Assert.Same(pad.SlotAvailableInputs, yawRow.AvailableInputs);
                // The modifier picker keeps the full list, and the Up and
                // Down pickers take the slot's keys from it.
                Assert.Same(pad.SlotAvailableInputs, gyroRow.ParamInputs);
                Assert.Same(pad.SlotAvailableInputs, yawRow.ParamInputs);
                Assert.Same(pad.SlotKeyInputs, gyroRow.KeyInputs);
                Assert.Contains(pad.SlotKeyInputs, c => c.Descriptor == "Button 0" && c.DeviceGuid == key);
                Assert.DoesNotContain(pad.SlotKeyInputs, c => c.Descriptor == "Gyro Pitch");
                Assert.Same(yawRow.AvailableInputsView, yawRow.ParamInputsView);
                Assert.NotSame(gyroRow.AvailableInputsView, gyroRow.ParamInputsView);

                // A modifier on the Motion Gyro row still resolves a button.
                var modifier = new MappingSourceItem { Kind = "InvertOnHold", DeviceGuid = key, ParamModifier = "Button 0" };
                gyroRow.ExtraSources.Add(modifier);
                Assert.NotNull(modifier.ParamModifierInputChoice);
                Assert.Equal("Button 0", modifier.ParamModifierInputChoice.Descriptor);

                // Hiding the device hides it in all three lists.
                pad.HiddenPickerDeviceKeys.Add(key);
                pad.ApplyMappingPickerFilter();
                foreach (var list in new[] { pad.SlotAvailableInputs, pad.SlotMotionGyroInputs, pad.SlotMotionAccelInputs })
                {
                    var view = CollectionViewSource.GetDefaultView(list);
                    Assert.DoesNotContain(view.Cast<InputChoice>(), c => c.DeviceGuid == key);
                }
            }
            finally
            {
                SettingsManager.UserDevices = savedDevices;
                SettingsManager.UserSettings = savedSettings;
            }
        }

        [Fact]
        public void TheDeviceFilterListsADeviceOnlyTheMotionRowsOffer()
        {
            var pad = new PadViewModel(0) { OutputType = VirtualControllerType.PlayStation };
            pad.SlotAvailableInputs.Add(new InputChoice { Descriptor = "Button 0", DeviceGuid = "pad", DeviceLabel = "Pad" });
            pad.SlotMotionGyroInputs.Add(new InputChoice { Descriptor = "Motion Gyro", DeviceGuid = "phone", DeviceLabel = "Phone" });
            pad.SlotMotionAccelInputs.Add(new InputChoice { Descriptor = "Motion Accel", DeviceGuid = "phone", DeviceLabel = "Phone" });
            pad.RebuildPickerDeviceFilterEntries();
            Assert.Equal(new[] { "pad", "phone" }, pad.PickerDeviceFilterEntries.Select(e => e.Key));
        }

        // ── The value column ──

        [Fact]
        public void TheValueColumnShowsEachMotionRowsCombinedValue()
        {
            var vm = new MainViewModel();
            var svc = new InputService(vm) { SettingsService = new SettingsService(vm) };
            var pad = vm.Pads[0];
            pad.OutputType = VirtualControllerType.PlayStation;
            vm.SelectedNavTag = "Pad1";
            using var manager = new InputManager();
            typeof(InputService).GetField("_inputManager", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(svc, manager);
            manager.CombinedMotionRows[0] = new PadForge.Engine.Common.MotionRowValues { Pitch = -1f, Yaw = 0.5f, Roll = 0f, Frame = 1 };
            typeof(InputService).GetMethod("UpdateMappingLiveValues", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(svc, null);

            MappingItem Item(string t) => pad.Mappings.Single(m => m.TargetSettingName == t);
            Assert.Equal("-32767", Item(MappingSetMigrator.MotionPitchTarget).CurrentValueText);
            Assert.True(Item(MappingSetMigrator.MotionPitchTarget).IsInputActive);
            Assert.Equal("16384", Item(MappingSetMigrator.MotionYawTarget).CurrentValueText);
            Assert.True(Item(MappingSetMigrator.MotionYawTarget).IsInputActive);
            Assert.Equal("0", Item(MappingSetMigrator.MotionRollTarget).CurrentValueText);
            Assert.False(Item(MappingSetMigrator.MotionRollTarget).IsInputActive);
        }

        [Fact]
        public void TheRestAtZeroRuleIsWiredWithTheOtherProviders()
        {
            // The engine reads a trigger one way on the motion rows only when
            // InputService hands it the activator test at start.
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string svc = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "PadForge.App", "Services", "InputService.cs"));
            Assert.Contains("SourceCoercion.SourceRestsAtZeroProvider =", svc);
            Assert.Contains("Common.Input.InputManager.SourceRestsAtZero;", svc);
            Assert.Contains("SourceCoercion.SourceRestsAtZeroProvider = null;", svc);
        }

        // ── The recorder ──

        private static UserDevice Sensors(bool aux = true) => new()
        {
            InstanceGuid = Guid.NewGuid(), CapType = InputDeviceType.Gamepad,
            HasGyro = true, HasAccel = true, HasGyroAux = aux, HasAccelAux = aux,
        };

        private static CustomInputState State(float[] gyro = null, float[] accel = null,
            float[] gyroAux = null, float[] accelAux = null)
        {
            var s = new CustomInputState();
            if (gyro != null) Array.Copy(gyro, s.Gyro, 3);
            if (accel != null) Array.Copy(accel, s.Accel, 3);
            if (gyroAux != null) Array.Copy(gyroAux, s.GyroAux, 3);
            if (accelAux != null) Array.Copy(accelAux, s.AccelAux, 3);
            return s;
        }

        private const float G = 9.80665f;

        [Fact]
        public void AGyroRecordsOnADeliberateTurn()
        {
            var ud = Sensors();
            var rest = State();
            Assert.Null(RecorderService.DetectMotion(1, ud, State(gyro: new[] { 0.4f, 0.4f, 0.4f }), rest));
            Assert.Equal("Motion Gyro", RecorderService.DetectMotion(1, ud, State(gyro: new[] { 0f, 2f, 0f }), rest));
            Assert.Equal("Motion Gyro L", RecorderService.DetectMotion(1, ud,
                State(gyro: new[] { 0f, 2f, 0f }, gyroAux: new[] { 3f, 0f, 0f }), rest));
            // A sensor the device lacks never records.
            var noAux = Sensors(aux: false);
            Assert.Equal("Motion Gyro", RecorderService.DetectMotion(1, noAux,
                State(gyro: new[] { 0f, 2f, 0f }, gyroAux: new[] { 3f, 0f, 0f }), rest));
            noAux.HasGyro = false;
            Assert.Null(RecorderService.DetectMotion(1, noAux, State(gyro: new[] { 0f, 2f, 0f }), rest));
            // Not a Motion recording.
            Assert.Null(RecorderService.DetectMotion(0, ud, State(gyro: new[] { 0f, 9f, 0f }), rest));
        }

        /// <summary>A pad whose gyro rests off zero records only when it
        /// turns. A DualShock 3 on PadForge's own path rests near 2.7 rad/s on
        /// its yaw (Ds3DirectService.YawFromWord, word 727), past the
        /// threshold, and the recording took it at once.</summary>
        [Fact]
        public void AGyroRestingOffZeroRecordsOnlyWhenItTurns()
        {
            var ud = Sensors(aux: false);
            float offset = (727 - 512) * PadForge.Engine.DualShock3Motion.GyroRadPerCount;
            var rest = State(gyro: new[] { 0f, offset, 0f });
            Assert.Null(RecorderService.DetectMotion(1, ud, State(gyro: new[] { 0f, offset, 0f }), rest));
            Assert.Null(RecorderService.DetectMotion(1, ud, State(gyro: new[] { 0.3f, offset + 0.4f, 0f }), rest));
            Assert.Equal("Motion Gyro", RecorderService.DetectMotion(1, ud, State(gyro: new[] { 0f, offset + 2f, 0f }), rest));
        }

        [Fact]
        public void AnAccelerometerRecordsOnATiltOrAShake()
        {
            var ud = Sensors();
            var flat = State(accel: new[] { 0f, G, 0f }, accelAux: new[] { 0f, G, 0f });
            Assert.Null(RecorderService.DetectMotion(2, ud, State(accel: new[] { 0.2f * G, 0.98f * G, 0f }, accelAux: new[] { 0f, G, 0f }), flat));
            Assert.Equal("Motion Accel", RecorderService.DetectMotion(2, ud,
                State(accel: new[] { 0f, 0f, G }, accelAux: new[] { 0f, G, 0f }), flat));
            Assert.Equal("Motion Accel L", RecorderService.DetectMotion(2, ud,
                State(accel: new[] { 0f, 0.7f * G, 0.7f * G }, accelAux: new[] { 0f, -G, 0f }), flat));
        }

        [Fact]
        public void AnAccelerometerWithoutAStartingReadingTakesItsFirstAsTheReference()
        {
            var ud = Sensors();
            var empty = State();
            var resting = State(accel: new[] { 0f, G, 0f }, accelAux: new[] { 0f, G, 0f });
            // Gravity arriving is not a shake.
            Assert.Null(RecorderService.DetectMotion(2, ud, resting, empty));
            Assert.True(RecorderService.AccelReferenceMissing(ud, resting, empty));
            Assert.True(RecorderService.AccelReferenceMissing(ud, resting, null));
            Assert.Null(RecorderService.DetectMotion(2, ud, resting, null));
            // Once it is the reference, a tilt records.
            Assert.False(RecorderService.AccelReferenceMissing(ud, resting, resting));
            Assert.Equal("Motion Accel", RecorderService.DetectMotion(2, ud,
                State(accel: new[] { G, 0f, 0f }, accelAux: new[] { 0f, G, 0f }), resting));
            // A Nunchuk plugged in mid-recording.
            var bodyOnly = State(accel: new[] { 0f, G, 0f });
            Assert.Null(RecorderService.DetectMotion(2, ud, resting, bodyOnly));
            Assert.True(RecorderService.AccelReferenceMissing(ud, resting, bodyOnly));
            // A device that reports no accelerometer at all never asks for one.
            Assert.False(RecorderService.AccelReferenceMissing(ud, empty, empty));
        }

        /// <summary>A recording session on slot 0 with one controller, ticked
        /// by hand.</summary>
        private sealed class RecorderSession : IDisposable
        {
            private readonly DeviceCollection _devices = SettingsManager.UserDevices;
            private readonly SettingsCollection _settings = SettingsManager.UserSettings;
            private readonly MethodInfo _tick = typeof(RecorderService).GetMethod("PollTick", BindingFlags.Instance | BindingFlags.NonPublic);
            public MainViewModel ViewModel { get; } = new();
            public RecorderService Recorder { get; }
            public UserDevice Device { get; }
            public PadViewModel Pad => ViewModel.Pads[0];

            public RecorderSession(bool gyro, bool accel)
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                var state = new CustomInputState();
                Array.Fill(state.Axis, 32768);
                Device = new UserDevice
                {
                    InstanceGuid = Guid.NewGuid(), ProductName = "Pad", IsOnline = true,
                    CapType = InputDeviceType.Gamepad, InputState = state, HasGyro = gyro, HasAccel = accel,
                };
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(Device);
                Pad.OutputType = VirtualControllerType.PlayStation;
                Pad.MappedDevices.Add(new PadViewModel.MappedDeviceInfo { InstanceGuid = Device.InstanceGuid, Name = "Pad", IsOnline = true });
                Recorder = new RecorderService(ViewModel);
            }

            public MappingItem Row(string target) => Pad.Mappings.Single(m => m.TargetSettingName == target);
            public void Tick() => _tick.Invoke(Recorder, new object[] { null, EventArgs.Empty });

            public void Dispose()
            {
                if (Recorder.IsRecording) Recorder.CancelRecording();
                SettingsManager.UserDevices = _devices;
                SettingsManager.UserSettings = _settings;
            }
        }

        [Fact]
        public void RecordingTheMotionGyroRowWaitsForATurn()
        {
            using var session = new RecorderSession(gyro: true, accel: true);
            session.Device.InputState.Accel[1] = G;
            var row = session.Row(MappingSetMigrator.MotionGyroTarget);
            session.Recorder.StartRecording(row, 0, session.Device.InstanceGuid);
            Assert.True(session.Recorder.IsRecording);
            Assert.Equal(string.Format(Strings.Instance.Status_RecordingMotionPrompt_Format, row.TargetLabel), session.ViewModel.StatusText);

            // A stick push and a button press are not a turn.
            session.Device.InputState.Axis[0] = 65535;
            session.Device.InputState.Buttons[0] = true;
            session.Tick();
            session.Tick();
            Assert.True(session.Recorder.IsRecording);

            session.Device.InputState.Gyro[1] = 2f;
            session.Tick();
            Assert.False(session.Recorder.IsRecording);
            Assert.Equal(MappingSetMigrator.MotionGyroSourceDescriptor, row.SourceDescriptor);
            Assert.Equal(session.Device.InstanceGuidString.ToLowerInvariant(), row.PrimarySourceDeviceGuid);
        }

        [Fact]
        public void RecordingAMotionRowWithoutItsSensorDoesNotStart()
        {
            using var session = new RecorderSession(gyro: false, accel: true);
            bool ended = false;
            session.Recorder.RecordingTimedOut += (_, _) => ended = true;
            var gyroRow = session.Row(MappingSetMigrator.MotionGyroTarget);
            session.Recorder.StartRecording(gyroRow, 0, session.Device.InstanceGuid);
            Assert.False(session.Recorder.IsRecording);
            Assert.True(ended);
            Assert.False(gyroRow.IsRecording);
            Assert.Equal(string.Format(Strings.Instance.Status_NoMotionSensorToRecord_Format, gyroRow.TargetLabel),
                session.ViewModel.StatusText);

            // The same controller's accelerometer records.
            session.Recorder.StartRecording(session.Row(MappingSetMigrator.MotionAccelTarget), 0, session.Device.InstanceGuid);
            Assert.True(session.Recorder.IsRecording);
        }

        [Fact]
        public void AnAccelerometerThatStartsReportingMidRecordingIsNotAShake()
        {
            using var session = new RecorderSession(gyro: false, accel: true);
            var row = session.Row(MappingSetMigrator.MotionAccelTarget);
            session.Recorder.StartRecording(row, 0, session.Device.InstanceGuid);
            Assert.True(session.Recorder.IsRecording);

            session.Device.InputState.Accel[1] = G;     // the stream starts
            session.Tick();
            session.Tick();
            Assert.True(session.Recorder.IsRecording);

            session.Device.InputState.Accel[1] = 0f;    // tipped onto its face
            session.Device.InputState.Accel[2] = G;
            session.Tick();
            Assert.False(session.Recorder.IsRecording);
            Assert.Equal(MappingSetMigrator.MotionAccelSourceDescriptor, row.SourceDescriptor);
        }

        [Fact]
        public void AModifierRecordedForTheMotionGyroRowIsAButton()
        {
            using var session = new RecorderSession(gyro: true, accel: true);
            var row = session.Row(MappingSetMigrator.MotionGyroTarget);
            var modifier = new MappingSourceItem { Kind = "InvertOnHold" };
            row.ExtraSources.Add(modifier);
            session.Recorder.StartRecordingExtraSourceParam(row, modifier, 0, RecorderService.ParamTarget.Modifier);
            Assert.True(session.Recorder.IsRecording);
            session.Device.InputState.Buttons[2] = true;
            session.Tick();
            Assert.False(session.Recorder.IsRecording);
            Assert.Equal("Button 2", modifier.ParamModifier);
        }

        [Theory]
        [InlineData("LeftThumbAxisY", "MotionPitch", 1, 0, false)]
        [InlineData("LeftThumbAxisY", "MotionPitch", 1, 65535, true)]
        [InlineData("LeftThumbAxisX", "MotionYaw", 0, 65535, false)]
        [InlineData("LeftThumbAxisX", "MotionRoll", 0, 0, true)]
        public void AMotionAxisRowRecordsAStickTheWayAStickRowDoes(string stick, string motion, int axis, int pushed, bool negRecording)
        {
            string Record(string target)
            {
                using var session = new RecorderSession(gyro: true, accel: true);
                var row = session.Row(target);
                session.Recorder.StartRecording(row, 0, session.Device.InstanceGuid, negRecording: negRecording);
                session.Device.InputState.Axis[axis] = pushed;
                for (int i = 0; i < 20 && session.Recorder.IsRecording; i++) session.Tick();
                Assert.False(session.Recorder.IsRecording, $"{target} never recorded");
                return $"{row.SourceDescriptor} inverted={row.IsInverted} half={row.IsHalfAxis}";
            }
            Assert.Equal(Record(stick), Record(motion));
        }

        [Fact]
        public void TheMotionPitchRowRecordsAStickLikeAStickRow()
        {
            using var session = new RecorderSession(gyro: true, accel: true);
            var row = session.Row(MappingSetMigrator.MotionPitchTarget);
            session.Recorder.StartRecording(row, 0, session.Device.InstanceGuid);
            Assert.Equal(string.Format(Strings.Instance.Status_RecordingPrompt_Format, row.TargetLabel), session.ViewModel.StatusText);
            // Turning the controller records nothing here.
            session.Device.InputState.Gyro[0] = 3f;
            session.Tick();
            Assert.True(session.Recorder.IsRecording);
        }
    }
}
