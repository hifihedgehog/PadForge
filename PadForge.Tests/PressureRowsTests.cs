using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HIDMaestro;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The button pressure rows (discussion #476). HIDMaestro 1.10.0's
    /// DualShock 3 (SIXAXIS): Full preset carries how hard cross, circle,
    /// square, triangle, L1, R1 and the D-pad are pressed, and these rows
    /// feed it: through the real Step 3 and Step 4, the wire gate, the grid,
    /// the recorder and the default mapping.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class PressureRowsTests
    {
        private const string Full = "dualshock-3-full";
        private const VirtualControllerType PS = VirtualControllerType.PlayStation;
        private static readonly string[] Targets = MappingSetMigrator.PressureTargets.ToArray();

        private sealed class Pad : WebControllerDevice, ISdlInputDevice
        {
            public Pad() : base(Guid.NewGuid().ToString(), "Pressure Rows Pad") { }
            public bool HasGyroAux { get; set; }
            public bool HasAccelAux { get; set; }
        }

        private static MappingSource Source(UserDevice d, string descriptor)
            => new() { Kind = "Direct", DeviceGuid = d.InstanceGuidString, Descriptor = descriptor };

        private static MappingRow Row(string target, params MappingSource[] sources)
            => new() { Target = target, LayerMask = "Base", Sources = new List<MappingSource>(sources) };

        private static UserSetting Setting(UserDevice d)
            => SettingsManager.UserSettings.Items.First(us => us.InstanceGuid == d.InstanceGuid);

        /// <summary>Step 3 and Step 4 of a real InputManager over settings
        /// built by hand.</summary>
        private sealed class Rig : IDisposable
        {
            private readonly SettingsCollection _settings = SettingsManager.UserSettings;
            private readonly DeviceCollection _devices = SettingsManager.UserDevices;
            private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
            private readonly bool[] _created = SettingsManager.SlotCreated;
            private readonly bool[] _enabled = SettingsManager.SlotEnabled;
            private readonly List<Pad> _wrappers = new();
            private readonly Action _step3, _step4, _neutralize;
            public InputManager Manager { get; } = new();

            public Rig()
            {
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
                SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
                SettingsManager.SlotEnabled = new bool[InputManager.MaxPads];
                Action Bind(string name) => typeof(InputManager).GetMethod(name,
                    BindingFlags.Instance | BindingFlags.NonPublic).CreateDelegate<Action>(Manager);
                _step3 = Bind("UpdateOutputStates");
                _step4 = Bind("CombineOutputStates");
                _neutralize = Bind("NeutralizeCombinedOutputs");
            }

            public UserDevice AddDevice(int slot)
            {
                var wrapper = new Pad();
                _wrappers.Add(wrapper);
                var state = new CustomInputState();
                for (int i = 0; i < 6; i++) state.Axis[i] = 32768;
                var device = new UserDevice
                {
                    InstanceGuid = Guid.NewGuid(), Device = wrapper, InputState = state,
                    IsOnline = true, CapType = InputDeviceType.Gamepad,
                };
                device.LoadCapabilities(16, 17, 1, InputDeviceType.Gamepad);
                SettingsManager.UserDevices.Items.Add(device);
                var setting = new UserSetting { InstanceGuid = device.InstanceGuid, MapTo = slot };
                setting.SetPadSetting(new PadSetting());
                SettingsManager.UserSettings.Items.Add(setting);
                return device;
            }

            public MappingSet Slot(int slot, VirtualControllerType type, string profile, params MappingRow[] rows)
            {
                SettingsManager.SlotCreated[slot] = true;
                SettingsManager.SlotEnabled[slot] = true;
                Manager.SlotControllerTypes[slot] = type;
                Manager.SlotProfileIds[slot] = profile;
                var set = new MappingSet();
                set.Rows.AddRange(rows);
                SettingsManager.SlotMappingSets[slot] = set;
                return set;
            }

            public void Poll()
            {
                _step3();
                _step4();
            }

            public void Neutralize() => _neutralize();

            public void Dispose()
            {
                try
                {
                    Manager.Dispose();
                    foreach (var w in _wrappers) w.Dispose();
                }
                finally
                {
                    SettingsManager.UserSettings = _settings;
                    SettingsManager.UserDevices = _devices;
                    SettingsManager.SlotMappingSets = _sets;
                    SettingsManager.SlotCreated = _created;
                    SettingsManager.SlotEnabled = _enabled;
                }
            }
        }

        // ── Step 3 and Step 4 ──

        [Fact]
        public void ACrossPressReachesTheSlotWithItsPressure()
        {
            using var rig = new Rig();
            var pad = rig.AddDevice(0);
            rig.Slot(0, PS, Full,
                Row("ButtonA", Source(pad, "Button 0")),
                Row("PressureButtonA", Source(pad, "Axis 6")));
            pad.InputState.Buttons[0] = true;
            pad.InputState.Axis[6] = 128 * 257;
            rig.Poll();

            Assert.Equal(128, rig.Manager.CombinedPressureStates[0].ButtonA);
            Assert.NotEqual(0, rig.Manager.CombinedOutputStates[0].Buttons & Gamepad.A);
            var wire = HMaestroVirtualController.PressureOnTheWire(
                rig.Manager.CombinedPressureStates[0], rig.Manager.CombinedOutputStates[0]);
            Assert.Equal(128, wire.ButtonA);
        }

        [Fact]
        public void EveryPressureByteRoundTrips()
        {
            using var rig = new Rig();
            var pad = rig.AddDevice(0);
            rig.Slot(0, PS, Full, Row("PressureButtonA", Source(pad, "Axis 6")));
            for (int b = 0; b <= 255; b++)
            {
                // A DualShock 3 pressure byte reaches Axis[] times 257
                // (Ds3DirectService.PressureAxis, SDL_hidapi_ps3.c).
                pad.InputState.Axis[6] = b * 257;
                rig.Poll();
                Assert.Equal(b, rig.Manager.CombinedPressureStates[0].ButtonA);
            }
        }

        [Fact]
        public void EachTargetReadsItsOwnAxis()
        {
            using var rig = new Rig();
            var pad = rig.AddDevice(0);
            rig.Slot(0, PS, Full,
                Targets.Select((t, i) => Row(t, Source(pad, $"Axis {6 + i}"))).ToArray());
            for (int i = 0; i < Targets.Length; i++) pad.InputState.Axis[6 + i] = (10 + i) * 257;
            rig.Poll();

            var p = rig.Manager.CombinedPressureStates[0];
            for (int i = 0; i < Targets.Length; i++) Assert.Equal(10 + i, p[i]);
            // By name too, so a swap inside the indexer cannot hide.
            Assert.Equal(10, p.ButtonA);
            Assert.Equal(11, p.ButtonB);
            Assert.Equal(12, p.ButtonX);
            Assert.Equal(13, p.ButtonY);
            Assert.Equal(14, p.LeftShoulder);
            Assert.Equal(15, p.RightShoulder);
            Assert.Equal(16, p.DPadUp);
            Assert.Equal(17, p.DPadDown);
            Assert.Equal(18, p.DPadLeft);
            Assert.Equal(19, p.DPadRight);
        }

        [Theory]
        [InlineData(VirtualControllerType.PlayStation, "dualshock-3")]
        [InlineData(VirtualControllerType.PlayStation, "dualshock-4-v2")]
        [InlineData(VirtualControllerType.PlayStation, "dualsense")]
        [InlineData(VirtualControllerType.Xbox, "xbox-360-wired")]
        [InlineData(VirtualControllerType.Extended, Full)]
        public void APresetWithoutPressureReadsNone(VirtualControllerType type, string profile)
        {
            using var rig = new Rig();
            var pad = rig.AddDevice(0);
            var control = rig.AddDevice(1);
            rig.Slot(0, type, profile, Row("PressureButtonA", Source(pad, "Axis 6")));
            rig.Slot(1, PS, Full, Row("PressureButtonA", Source(control, "Axis 6")));
            pad.InputState.Axis[6] = 200 * 257;
            control.InputState.Axis[6] = 200 * 257;
            rig.Poll();

            Assert.Equal(default(ButtonPressureState), rig.Manager.CombinedPressureStates[0]);
            // Same poll, same rows, the Full preset: the read is live.
            Assert.Equal(200, rig.Manager.CombinedPressureStates[1].ButtonA);
        }

        [Fact]
        public void APressureRowWritesNoGamepadField()
        {
            using var rig = new Rig();
            var pad = rig.AddDevice(0);
            rig.Slot(0, PS, Full, Row("PressureButtonA", Source(pad, "Button 1")));
            pad.InputState.Buttons[1] = true;
            rig.Poll();

            Assert.Equal(255, rig.Manager.CombinedPressureStates[0].ButtonA);
            var gp = rig.Manager.CombinedOutputStates[0];
            Assert.Equal(0, gp.Buttons);
            Assert.Equal(0, gp.LeftTrigger);
            Assert.Equal(0, gp.RightTrigger);
        }

        [Fact]
        public void TheHarderPressWinsAcrossTheSlotsDevices()
        {
            using var rig = new Rig();
            var a = rig.AddDevice(0);
            var b = rig.AddDevice(0);
            // An "(Any Device)" source reads each pass's own device.
            rig.Slot(0, PS, Full, Row("PressureButtonA",
                new MappingSource { Kind = "Direct", DeviceGuid = "", Descriptor = "Axis 6" }));
            a.InputState.Axis[6] = 100 * 257;
            b.InputState.Axis[6] = 200 * 257;
            rig.Poll();

            Assert.Equal(100, Setting(a).PressureOutputState.ButtonA);
            Assert.Equal(200, Setting(b).PressureOutputState.ButtonA);
            Assert.Equal(200, rig.Manager.CombinedPressureStates[0].ButtonA);
        }

        [Fact]
        public void APadThatLeavesTakesItsPressureWithItAndABriefDropHoldsIt()
        {
            using var rig = new Rig();
            var pad = rig.AddDevice(0);
            rig.Slot(0, PS, Full,
                Row("ButtonA", Source(pad, "Button 0")),
                Row("PressureButtonA", Source(pad, "Axis 6")));
            pad.InputState.Buttons[0] = true;
            pad.InputState.Axis[6] = 255 * 257;
            rig.Poll();
            Assert.Equal(255, rig.Manager.CombinedPressureStates[0].ButtonA);

            // Briefly unavailable: the pressure holds with the press it
            // belongs to, the OutputState rule.
            pad.IsOnline = false;
            rig.Poll();
            Assert.Equal(255, rig.Manager.CombinedPressureStates[0].ButtonA);
            Assert.NotEqual(0, rig.Manager.CombinedOutputStates[0].Buttons & Gamepad.A);

            // Gone: both go to rest.
            pad.IsOnline = true;
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Remove(pad);
            rig.Poll();
            Assert.Equal(default(ButtonPressureState), Setting(pad).PressureOutputState);
            Assert.Equal(0, rig.Manager.CombinedPressureStates[0].ButtonA);
        }

        [Fact]
        public void TheFocusSuspendClearsThePressure()
        {
            using var rig = new Rig();
            var pad = rig.AddDevice(0);
            rig.Slot(0, PS, Full, Row("PressureButtonA", Source(pad, "Axis 6")));
            pad.InputState.Axis[6] = 90 * 257;
            rig.Poll();
            Assert.Equal(90, rig.Manager.CombinedPressureStates[0].ButtonA);
            rig.Neutralize();
            Assert.Equal(default(ButtonPressureState), rig.Manager.CombinedPressureStates[0]);
        }

        [Fact]
        public void OnlyARowWithAnInputWakesThePressurePass()
        {
            Assert.False(InputManager.HasSourcedPressureRow(null));
            var set = new MappingSet();
            set.Rows.Add(Row("ButtonA", new MappingSource { Kind = "Direct", Descriptor = "Button 0" }));
            Assert.False(InputManager.HasSourcedPressureRow(set));
            set.Rows.Add(Row("PressureButtonA", new MappingSource { Kind = "InvertOnHold", ParamModifier = "Button 4" }));
            Assert.False(InputManager.HasSourcedPressureRow(set));
            set.Rows.Add(Row("PressureDPadLeft", new MappingSource { Kind = "Direct", Descriptor = "Axis 14" }));
            Assert.True(InputManager.HasSourcedPressureRow(set));
        }

        /// <summary>A pressure row whose result is positive but under one step
        /// of the trigger scale still reads pressed, at the lightest pressure.
        /// It truncated to released, and HIDMaestro sends a pressed button
        /// with no pressure behind it as a full press. A zero result stays
        /// released, and an ordinary trigger keeps its exact
        /// quantization.</summary>
        [Fact]
        public void APositivePressureBelowOneStepStaysPressed()
        {
            var state = new CustomInputState();
            state.Buttons[0] = true;
            static MappingSet Set(string target, string expression)
            {
                var row = Row(target,
                    new MappingSource { Kind = "Direct", Descriptor = "Button 0" },
                    new MappingSource { Kind = "Direct", Descriptor = "Button 1" });
                row.CombineMode = "Custom";
                row.CombineExpression = expression;
                var set = new MappingSet();
                set.Rows.Add(row);
                return set;
            }

            foreach (string target in MappingSetMigrator.PressureTargets)
            {
                Assert.True(InputManager.TryEvaluateMappingSetRawTrigger(state, Set(target, "a * 0.000001"), "", 6, target, out short v));
                Assert.Equal(short.MinValue + 1, v);
                Assert.Equal(1, InputManager.PressureByte(v));
                Assert.True(InputManager.TryEvaluateMappingSetRawTrigger(state, Set(target, "a * 0"), "", 6, target, out v));
                Assert.Equal(short.MinValue, v);
            }
            Assert.True(InputManager.TryEvaluateMappingSetRawTrigger(state, Set("RawAxis5", "a * 0.000001"), "", 6, "RawAxis5", out short t));
            Assert.Equal(short.MinValue, t);
        }

        [Fact]
        public void APressureByteRoundsFromTheTriggerScale()
        {
            Assert.Equal(0, InputManager.PressureByte(short.MinValue));
            Assert.Equal(255, InputManager.PressureByte(short.MaxValue));
            Assert.Equal(128, InputManager.PressureByte((short)(128 * 257 + short.MinValue)));
            // Rounded, not truncated: 200 of 65535 is 0.78 of a step.
            Assert.Equal(1, InputManager.PressureByte((short)(200 + short.MinValue)));
            // A press below half a step is still 1, never the 0 HIDMaestro
            // sends as a full press on a pressed button.
            Assert.Equal(1, InputManager.PressureByte((short)(100 + short.MinValue)));
            Assert.Equal(1, InputManager.PressureByte((short)(1 + short.MinValue)));
            // Every DualShock 3 byte still round-trips exactly.
            for (int b = 0; b <= 255; b++)
                Assert.Equal(b, InputManager.PressureByte((short)(b * 257 + short.MinValue)));
        }

        [Fact]
        public void ADeviceTeardownRestsItsPressure()
        {
            using var rig = new Rig();
            var pad = rig.AddDevice(0);
            var us = Setting(pad);
            us.PressureOutputState = new ButtonPressureState { ButtonA = 200, DPadLeft = 50 };
            typeof(InputManager).GetMethod("NeutralizeMappedOutputsFor",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(rig.Manager, new object[] { pad });
            Assert.Equal(default(ButtonPressureState), us.PressureOutputState);
        }

        [Fact]
        public void ASlotThatLeavesTheFullPresetDropsItsPressure()
        {
            using var rig = new Rig();
            var pad = rig.AddDevice(0);
            rig.Slot(0, PS, Full, Row("PressureButtonA", Source(pad, "Axis 6")));
            pad.InputState.Axis[6] = 200 * 257;
            rig.Poll();
            Assert.Equal(200, rig.Manager.CombinedPressureStates[0].ButtonA);

            rig.Manager.SlotProfileIds[0] = "dualshock-3";
            rig.Poll();
            Assert.Equal(default(ButtonPressureState), rig.Manager.CombinedPressureStates[0]);
        }

        // ── The wire ──

        [Fact]
        public void APressureGoesOutOnlyWhileItsButtonIsPressed()
        {
            var p = new ButtonPressureState();
            for (int i = 0; i < ButtonPressureState.Count; i++) p[i] = (byte)(100 + i);

            var released = HMaestroVirtualController.PressureOnTheWire(p, default);
            Assert.Equal(default(ButtonPressureState), released);

            // Cross held, the pressure row reading 0: HIDMaestro sends that
            // as fully pressed, which is right for a macro or a key.
            var zero = HMaestroVirtualController.PressureOnTheWire(default,
                new Gamepad { Buttons = Gamepad.A });
            Assert.Equal(0, zero.ButtonA);
        }

        [Theory]
        [InlineData(Gamepad.A, 0)]
        [InlineData(Gamepad.B, 1)]
        [InlineData(Gamepad.X, 2)]
        [InlineData(Gamepad.Y, 3)]
        [InlineData(Gamepad.LEFT_SHOULDER, 4)]
        [InlineData(Gamepad.RIGHT_SHOULDER, 5)]
        [InlineData(Gamepad.DPAD_UP, 6)]
        [InlineData(Gamepad.DPAD_DOWN, 7)]
        [InlineData(Gamepad.DPAD_LEFT, 8)]
        [InlineData(Gamepad.DPAD_RIGHT, 9)]
        public void EachButtonCarriesOnlyItsOwnPressure(ushort button, int index)
        {
            var p = new ButtonPressureState();
            for (int i = 0; i < ButtonPressureState.Count; i++) p[i] = (byte)(1 + i);
            var wire = HMaestroVirtualController.PressureOnTheWire(p, new Gamepad { Buttons = button });
            for (int i = 0; i < ButtonPressureState.Count; i++)
                Assert.Equal(i == index ? 1 + i : 0, wire[i]);
        }

        [Fact]
        public void TheDPadPressureFollowsTheHat()
        {
            var p = new ButtonPressureState { DPadUp = 10, DPadDown = 20, DPadLeft = 30, DPadRight = 40 };

            // Up and down together: the hat reads north, so down sends none.
            var upDown = HMaestroVirtualController.PressureOnTheWire(p,
                new Gamepad { Buttons = Gamepad.DPAD_UP | Gamepad.DPAD_DOWN });
            Assert.Equal(10, upDown.DPadUp);
            Assert.Equal(0, upDown.DPadDown);

            var leftRight = HMaestroVirtualController.PressureOnTheWire(p,
                new Gamepad { Buttons = Gamepad.DPAD_LEFT | Gamepad.DPAD_RIGHT });
            Assert.Equal(30, leftRight.DPadLeft);
            Assert.Equal(0, leftRight.DPadRight);

            // A diagonal carries both.
            var upRight = HMaestroVirtualController.PressureOnTheWire(p,
                new Gamepad { Buttons = Gamepad.DPAD_UP | Gamepad.DPAD_RIGHT });
            Assert.Equal(10, upRight.DPadUp);
            Assert.Equal(40, upRight.DPadRight);
            Assert.Equal(0, upRight.DPadDown);
            Assert.Equal(0, upRight.DPadLeft);
        }

        /// <summary>A source pin, the repository's form for code with no
        /// seam: Step 5's PlayStation submit cannot run without a live
        /// HIDMaestro controller, and dropping the pressure from that call
        /// left every other test here passing.</summary>
        [Fact]
        public void Step5HandsTheSlotsPressureToTheVirtualController()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName,
                "PadForge.App", "Common", "Input", "InputManager.Step5.VirtualDevices.cs")).Replace("\r\n", "\n");
            int call = src.IndexOf("hmExtState.SubmitGamepadState(", StringComparison.Ordinal);
            Assert.True(call >= 0);
            int end = src.IndexOf(");", call, StringComparison.Ordinal);
            string args = src.Substring(call, end - call);
            Assert.Contains("gpOut,", args);
            Assert.Contains("CombinedPressureStates[padIndex]", args);
        }

        [Fact]
        public void EachPressureLandsInItsHIDMaestroField()
        {
            var p = new ButtonPressureState();
            for (int i = 0; i < ButtonPressureState.Count; i++) p[i] = (byte)(10 + i);
            var state = new HMGamepadState();
            HMaestroVirtualController.WritePressure(ref state, p);
            Assert.Equal(10, state.PressureA);           // cross, HMButton.A
            Assert.Equal(11, state.PressureB);           // circle
            Assert.Equal(12, state.PressureX);           // square
            Assert.Equal(13, state.PressureY);           // triangle
            Assert.Equal(14, state.PressureLeftBumper);  // L1
            Assert.Equal(15, state.PressureRightBumper); // R1
            Assert.Equal(16, state.PressureDpadUp);
            Assert.Equal(17, state.PressureDpadDown);
            Assert.Equal(18, state.PressureDpadLeft);
            Assert.Equal(19, state.PressureDpadRight);
        }

        // ── The preset ──

        [Theory]
        [InlineData("dualshock-3-full", true)]
        [InlineData("DualShock-3-Full", true)]
        [InlineData("dualshock-3", false)]
        [InlineData("dualshock-4-v2", false)]
        [InlineData("dualsense", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void OnlyTheFullPresetCarriesPressure(string id, bool expected)
            => Assert.Equal(expected, HMaestroProfileCatalog.ReportCarriesPressure(id));

        [Fact]
        public void TheFullPresetIsOfferedAndDrawsTheDualShock3()
        {
            Assert.Contains(HMaestroProfileCatalog.PlayStationProfiles, p => p.Id == Full);
            var folders = HMaestroProfileCatalog.ResolveAssetFolders(Full, PS, out bool dedicated);
            Assert.Equal(("DS3", "DS3"), folders);
            Assert.True(dedicated);
            // Its report carries the accelerometer and yaw.
            Assert.False(HMaestroProfileCatalog.ReportCarriesNoMotion(Full));
        }

        // ── The grid ──

        private static PadViewModel Grid(VirtualControllerType type, string profile)
            => RestoredPad.Build(0, type, profile);

        /// <summary>Sets a preset the way a restore does, stamp first, so the
        /// change never reads as live whatever an earlier test left stamped
        /// on slot 0 (SettingResetTests documents that trap).</summary>
        private static void Restore(PadViewModel vm, string profile)
        {
            string stamp = SettingsManager.GetWireStamp(vm.PadIndex);
            SettingsManager.StampNintendoWire(vm.PadIndex, profile);
            try { vm.ProfileId = profile; }
            finally { SettingsManager.StampNintendoWire(vm.PadIndex, stamp); }
        }

        [Fact]
        public void TheFullPresetAddsTenPressureRowsAfterR2()
        {
            var vm = Grid(PS, Full);
            int r2 = vm.Mappings.IndexOf(vm.Mappings.Single(m => m.TargetSettingName == "RightTrigger"));
            var rows = vm.Mappings.Skip(r2 + 1).Take(Targets.Length).ToList();
            Assert.Equal(Targets, rows.Select(m => m.TargetSettingName));
            Assert.Equal(Targets.Length, vm.Mappings.Count(m => MappingSetMigrator.IsPressureTarget(m.TargetSettingName)));

            var s = Strings.Instance;
            string[] buttons =
            {
                "✕", "○", "◻", "△", "L1", "R1",
                s.Btn_DPadUp, s.Btn_DPadDown, s.Btn_DPadLeft, s.Btn_DPadRight,
            };
            for (int i = 0; i < rows.Count; i++)
            {
                Assert.Equal(string.Format(s.DevObj_Pressure_Format, buttons[i]), rows[i].TargetLabel);
                Assert.Equal(MappingCategory.Triggers, rows[i].Category);
                Assert.False(rows[i].IncludeInMapAll, $"{rows[i].TargetSettingName} is in the Map All walk");
                Assert.True(rows[i].IsRecordable);
            }
        }

        [Theory]
        [InlineData(VirtualControllerType.PlayStation, null)]
        [InlineData(VirtualControllerType.PlayStation, "dualshock-3")]
        [InlineData(VirtualControllerType.PlayStation, "dualshock-4-v2")]
        [InlineData(VirtualControllerType.PlayStation, "dualsense-edge")]
        [InlineData(VirtualControllerType.Xbox, null)]
        [InlineData(VirtualControllerType.Nintendo, null)]
        [InlineData(VirtualControllerType.Extended, null)]
        public void AnyOtherGridHasNoPressureRows(VirtualControllerType type, string profile)
        {
            var vm = Grid(type, profile);
            Assert.DoesNotContain(vm.Mappings, m => MappingSetMigrator.IsPressureTarget(m.TargetSettingName));
        }

        [Fact]
        public void SwitchingPresetsAddsAndRemovesTheRows()
        {
            var vm = Grid(PS, "dualshock-4-v1");
            Assert.DoesNotContain(vm.Mappings, m => MappingSetMigrator.IsPressureTarget(m.TargetSettingName));
            Restore(vm, Full);
            Assert.Equal(Targets.Length, vm.Mappings.Count(m => MappingSetMigrator.IsPressureTarget(m.TargetSettingName)));
            Restore(vm, "dualshock-3");
            Assert.DoesNotContain(vm.Mappings, m => MappingSetMigrator.IsPressureTarget(m.TargetSettingName));
        }

        [Fact]
        public void ThePressureRowsReadLikeTriggers()
        {
            foreach (var t in Targets)
            {
                Assert.Equal(TargetKind.Trigger, TargetKindResolver.Resolve(t));
                var m = new MappingItem(t, t, MappingCategory.Triggers, includeInMapAll: false);
                Assert.False(m.IsTargetDiscrete);
                Assert.True(m.IsTriggerTarget);
                m.LoadDescriptor("Axis 6");
                Assert.False(m.IsDeadZoneApplicable, $"{t} offered a button's deadzone to an analog row");
                m.AddExtraSourceCommand.Execute(null);
                Assert.Equal("MaxAbs", m.CombineMode);
            }
            Assert.Null(MappingSetMigrator.PressedButtonTarget("ButtonA"));
            Assert.Equal("DPadLeft", MappingSetMigrator.PressedButtonTarget("PressureDPadLeft"));
            Assert.Equal(-1, MappingSetMigrator.PressureIndexOf("PressureButton"));
        }

        // ── The recorder ──

        private sealed class RecorderSession : IDisposable
        {
            private readonly DeviceCollection _devices = SettingsManager.UserDevices;
            private readonly SettingsCollection _settings = SettingsManager.UserSettings;
            private readonly MethodInfo _tick = typeof(RecorderService).GetMethod("PollTick", BindingFlags.Instance | BindingFlags.NonPublic);
            public MainViewModel ViewModel { get; } = new();
            public RecorderService Recorder { get; }
            public UserDevice Device { get; }
            public PadViewModel Pad => ViewModel.Pads[0];

            public RecorderSession()
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                var state = new CustomInputState();
                Array.Fill(state.Axis, 32768);
                // A DualShock 3's pressure axes rest at 0.
                for (int i = 6; i < 16; i++) state.Axis[i] = 0;
                Device = new UserDevice
                {
                    InstanceGuid = Guid.NewGuid(), ProductName = "Pad", IsOnline = true,
                    CapType = InputDeviceType.Gamepad, InputState = state,
                };
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(Device);
                Pad.OutputType = PS;
                Restore(Pad, Full);
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

        [Theory]
        [InlineData("PressureButtonA", "Axis 6")]
        [InlineData("ButtonA", "Button 0")]
        public void APressRecordsThePressureOnAPressureRowAndTheButtonOnAButtonRow(string target, string expected)
        {
            using var session = new RecorderSession();
            var row = session.Row(target);
            session.Recorder.StartRecording(row, 0, session.Device.InstanceGuid);
            Assert.True(session.Recorder.IsRecording);

            // One press of cross moves both, the button in the first tick.
            session.Device.InputState.Buttons[0] = true;
            session.Device.InputState.Axis[6] = 65535;
            for (int i = 0; i < 10 && session.Recorder.IsRecording; i++) session.Tick();

            Assert.False(session.Recorder.IsRecording);
            Assert.Equal(expected, row.SourceDescriptor);
        }

        // ── The default mapping ──

        [Theory]
        [InlineData(0x054C, 0x0268, 16, 11, true)]   // SDL's PS3 driver
        [InlineData(0x054C, 0x0268, 16, 15, true)]   // PadForge's own reader
        [InlineData(0x054C, 0x0268, 16, 17, false)]  // DsHidMini SDF: another pressure order
        [InlineData(0x054C, 0x0268, 6, 11, false)]   // no pressure axes
        [InlineData(0x054C, 0x042F, 16, 15, false)]  // the Navigation controller
        [InlineData(0x054C, 0x05C4, 16, 11, false)]  // a DualShock 4
        public void TheDefaultMappingKnowsADualShock3ByItsShape(int vid, int pid, int axes, int buttons, bool expected)
            => Assert.Equal(expected, ButtonPressureSources.IsSdlOrderDualShock3((ushort)vid, (ushort)pid, axes, buttons));

        private static UserDevice Ds3(int rawButtons = 11)
        {
            var w = new SdlDeviceWrapper();
            void Set(string name, object value) => typeof(SdlDeviceWrapper).GetProperty(name).SetValue(w, value);
            Set(nameof(SdlDeviceWrapper.VendorId), (ushort)0x054C);
            Set(nameof(SdlDeviceWrapper.ProductId), (ushort)0x0268);
            Set(nameof(SdlDeviceWrapper.RawAxisCount), 16);
            Set(nameof(SdlDeviceWrapper.RawButtonCount), rawButtons);
            return new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), VendorId = 0x054C, ProdId = 0x0268,
                Device = w, IsOnline = true, CapType = InputDeviceType.Gamepad,
            };
        }

        private static string Field(PadSetting ps, string target)
            => (string)typeof(PadSetting).GetProperty(target).GetValue(ps);

        [Fact]
        public void ADualShock3OnTheFullPresetGetsItsTenPressureAxes()
        {
            var ds3 = Ds3();
            var sdf = Ds3(rawButtons: 17);
            try
            {
                var ps = SettingsManager.CreateDefaultPadSetting(ds3, PS, Full);
                Assert.Equal(Enumerable.Range(6, 10).Select(i => $"Axis {i}"), Targets.Select(t => Field(ps, t)));

                // Presets without pressure, and a pad PadForge cannot read it
                // from in this order, get none.
                foreach (var other in new[]
                {
                    SettingsManager.CreateDefaultPadSetting(ds3, PS, "dualshock-3"),
                    SettingsManager.CreateDefaultPadSetting(ds3, VirtualControllerType.Xbox, "xbox-360-wired"),
                    SettingsManager.CreateDefaultPadSetting(sdf, PS, Full),
                })
                    Assert.All(Targets, t => Assert.Equal("", Field(other, t)));
            }
            finally
            {
                (ds3.Device as IDisposable)?.Dispose();
                (sdf.Device as IDisposable)?.Dispose();
            }
        }

        /// <summary>A DualShock 3 as its cached entry keeps it while the pad is
        /// not connected: no device object, the raw button count folded into
        /// the 22 gamepad positions, and the SDL GUID of the driver that last
        /// opened it. The defaults are a real entry's: PadForge's own reader
        /// over Bluetooth, GUID ff00788c4c0500006802000000007601.</summary>
        private static UserDevice CachedDs3(string signature = "76", int rawAxes = 16, ushort pid = 0x0268)
        {
            string guid = "ff00788c4c050000" + $"{pid & 0xFF:x2}{pid >> 8:x2}" + "00000000" + signature + "01";
            var objects = new List<DeviceObjectItem>();
            for (int a = 0; a < rawAxes; a++)
                objects.Add(new DeviceObjectItem { InputIndex = a, ObjectType = DeviceObjectTypeFlags.AbsoluteAxis });
            objects.Add(new DeviceObjectItem { InputIndex = 0, ObjectType = DeviceObjectTypeFlags.PointOfViewController });
            for (int b = 0; b < 11; b++)
                objects.Add(new DeviceObjectItem { InputIndex = b, ObjectType = DeviceObjectTypeFlags.PushButton });
            return new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), VendorId = 0x054C, ProdId = pid,
                SdlGuid = guid, RawAxisCount = rawAxes, RawButtonCount = 22,
                CapType = InputDeviceType.Gamepad, HasGyro = true, HasAccel = true,
                DeviceObjects = objects.ToArray(),
            };
        }

        [Fact]
        public void ACachedDualShock3MapsItsPressureLikeAConnectedOne()
        {
            var cached = CachedDs3();
            Assert.Null(cached.Device);
            Assert.Equal(32, cached.SdlGuid.Length);
            Assert.Equal("ff00788c4c0500006802000000007601", cached.SdlGuid);

            var ps = SettingsManager.CreateDefaultPadSetting(cached, PS, Full);
            Assert.Equal(Enumerable.Range(6, 10).Select(i => $"Axis {i}"), Targets.Select(t => Field(ps, t)));
            // The rest of the pad maps one to one as before.
            Assert.Equal("Button 0", ps.ButtonA);
            Assert.Equal("Button 6", ps.ButtonBack);
            Assert.Equal("Button 10", ps.ButtonGuide);
            Assert.Equal("Axis 2", ps.LeftTrigger);
            Assert.Equal("POV 0 Up", ps.DPadUp);
            Assert.Equal("Motion Gyro", ps.MotionGyro);

            // A preset without pressure, or another slot type, gets none.
            foreach (var other in new[]
            {
                SettingsManager.CreateDefaultPadSetting(cached, PS, "dualshock-3"),
                SettingsManager.CreateDefaultPadSetting(cached, VirtualControllerType.Xbox, "xbox-360-wired"),
            })
                Assert.All(Targets, t => Assert.Equal("", Field(other, t)));
        }

        /// <summary>The driver byte stands in for the raw button count the
        /// entry folds away: SDL's PS3 driver ('h', 11 buttons) and PadForge's
        /// own reader ('v', 15) post the pressures in SDL's order, and any
        /// other driver gives no answer. DsHidMini's SDF mode, whose report
        /// SDL's sixaxis driver cannot open (InputManager's hint comment),
        /// reaches SDL through another driver with its own order.</summary>
        [Theory]
        [InlineData("76", 16, 0x0268, true)]    // PadForge's own reader
        [InlineData("68", 16, 0x0268, true)]    // SDL's PS3 driver
        [InlineData("68", 6, 0x0268, false)]    // the PS3 driver without analog buttons
        [InlineData("00", 16, 0x0268, false)]   // DirectInput
        [InlineData("72", 16, 0x0268, false)]   // RawInput
        [InlineData("78", 16, 0x0268, false)]   // XInput
        [InlineData("76", 16, 0x042F, false)]   // PadForge's Navigation controller reader
        public void ACachedEntryAnswersByTheDriverThatOpenedIt(string signature, int rawAxes, int pid, bool expected)
            => Assert.Equal(expected, ButtonPressureSources.AxesFor(CachedDs3(signature, rawAxes, (ushort)pid)) != null);

        [Fact]
        public void ACachedEntryWithoutAGuidOrWithAnotherDeviceObjectGetsNone()
        {
            var noGuid = CachedDs3();
            noGuid.SdlGuid = "";
            Assert.Null(ButtonPressureSources.AxesFor(noGuid));

            // A connected device that is not an SDL joystick answers from
            // itself, never from the cached fields.
            var other = CachedDs3();
            using var pad = new Pad();
            other.Device = pad;
            Assert.Null(ButtonPressureSources.AxesFor(other));
            Assert.Null(ButtonPressureSources.AxesFor(null));
        }

        /// <summary>A DualShock 3 shared over Remote Link answers from the
        /// owner's raw counts the link carries: SDL's PS3 driver (11 buttons)
        /// and PadForge's reader (15) put the pressures on axes 6 to 15,
        /// DsHidMini's SDF (17) does not, and an old peer sends no count.
        /// The connected proxy fell to the default arm and got nothing,
        /// while the same pad assigned offline mapped all ten.</summary>
        [Theory]
        [InlineData(11, true)]
        [InlineData(15, true)]
        [InlineData(17, false)]
        [InlineData(0, false)]
        public void ASharedDualShock3AnswersFromItsOwnersCounts(int rawButtons, bool answers)
        {
            var info = new PadForge.Engine.RemoteLink.RemotePeerDeviceInfo
            {
                VendorId = 0x054C, ProductId = 0x0268, NumAxes = 6, NumButtons = 22,
                RawAxisCount = 16, RawButtonCount = rawButtons,
            };
            var shared = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), VendorId = 0x054C, ProdId = 0x0268,
                Device = new PadForge.Engine.RemoteLink.RemotePeerDevice(info),
                IsOnline = true, CapType = InputDeviceType.Gamepad,
            };
            var axes = ButtonPressureSources.AxesFor(shared);
            if (answers) Assert.Equal(Enumerable.Range(6, 10), axes);
            else Assert.Null(axes);
        }

        [Fact]
        public void ChangingToTheFullPresetFillsACachedDualShock3()
        {
            var devices = SettingsManager.UserDevices;
            var settings = SettingsManager.UserSettings;
            var cached = CachedDs3();
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(cached);
                var ps = new PadSetting { ButtonA = "Button 0" };
                var us = new UserSetting { InstanceGuid = cached.InstanceGuid, MapTo = 0 };
                us.SetPadSetting(ps);
                lock (SettingsManager.UserSettings.SyncRoot) SettingsManager.UserSettings.Items.Add(us);

                Assert.True(DeviceService.FillEmptyPressureMappingsForSlot(0, Full));
                Assert.Equal("Axis 6", ps.PressureButtonA);
                Assert.Equal("Axis 15", ps.PressureDPadRight);
            }
            finally
            {
                SettingsManager.UserDevices = devices;
                SettingsManager.UserSettings = settings;
            }
        }

        // ── An original Xbox controller (discussion #483) ──

        /// <summary>The targets an original Xbox controller has a pressure
        /// for: its A, B, X, Y, White and Black on the fork's axes 6 to 11.
        /// The D-pad targets stay empty.</summary>
        private static readonly string[] XidTargets = Targets.Take(6).ToArray();

        [Theory]
        [InlineData(0x045E, 0x0202, 12, 11, true)]   // the Duke
        [InlineData(0x045E, 0x0285, 12, 11, true)]   // the Japanese Duke
        [InlineData(0x045E, 0x0287, 12, 11, true)]   // the Controller S
        [InlineData(0x0738, 0x4540, 12, 15, true)]   // a dance pad's 15 buttons (Mad Catz Beat Pad)
        [InlineData(0x045E, 0x0202, 12, 12, true)]   // a light gun's 12 buttons
        [InlineData(0x0A7B, 0xD000, 9, 46, false)]   // the Steel Battalion: another report
        [InlineData(0x045E, 0x0202, 6, 11, false)]   // no pressure axes
        [InlineData(0x045E, 0x0202, 12, 17, false)]  // a button count the XID driver never opens with
        [InlineData(0x045E, 0x028E, 12, 11, false)]  // an Xbox 360 pad: not in the XID table
        [InlineData(0x054C, 0x0268, 12, 11, false)]  // a DualShock 3's ids
        public void TheDefaultMappingKnowsAnOriginalXboxControllerByItsShape(int vid, int pid, int axes, int buttons, bool expected)
            => Assert.Equal(expected, ButtonPressureSources.IsXidGamepad((ushort)vid, (ushort)pid, axes, buttons));

        /// <summary>An SDL GUID for a USB device: the bus, the ids, the
        /// driver signature byte (data[14]) and the fork's XID GUID byte
        /// (data[15], 0x01 for the Duke).</summary>
        private static string XidGuid(string signature = "68", ushort vid = 0x045E, ushort pid = 0x0202)
            => "03000000" + $"{vid & 0xFF:x2}{vid >> 8:x2}0000" + $"{pid & 0xFF:x2}{pid >> 8:x2}0000" + "0000" + signature + "01";

        /// <summary>An original Xbox controller as the fork's XID driver
        /// opens it: 12 axes, 11 buttons, through HIDAPI.</summary>
        private static UserDevice Xid(string signature = "68", ushort vid = 0x045E, ushort pid = 0x0202)
        {
            var w = new SdlDeviceWrapper();
            void Set(string name, object value) => typeof(SdlDeviceWrapper).GetProperty(name).SetValue(w, value);
            Set(nameof(SdlDeviceWrapper.VendorId), vid);
            Set(nameof(SdlDeviceWrapper.ProductId), pid);
            Set(nameof(SdlDeviceWrapper.RawAxisCount), 12);
            Set(nameof(SdlDeviceWrapper.RawButtonCount), 11);
            Set(nameof(SdlDeviceWrapper.SdlGuid), XidGuid(signature, vid, pid));
            return new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), VendorId = vid, ProdId = pid,
                Device = w, IsOnline = true, CapType = InputDeviceType.Gamepad,
            };
        }

        [Fact]
        public void AnOriginalXboxControllerOnTheFullPresetGetsSixPressureAxes()
        {
            var xid = Xid();
            var rawInput = Xid(signature: "72");
            try
            {
                Assert.Equal(new[] { 6, 7, 8, 9, 10, 11, -1, -1, -1, -1 }, ButtonPressureSources.AxesFor(xid));
                var ps = SettingsManager.CreateDefaultPadSetting(xid, PS, Full);
                Assert.Equal(Enumerable.Range(6, 6).Select(i => $"Axis {i}"), XidTargets.Select(t => Field(ps, t)));
                Assert.All(Targets.Skip(6), t => Assert.Equal("", Field(ps, t)));

                // Presets without pressure, and the same ids through another
                // driver, get none.
                foreach (var other in new[]
                {
                    SettingsManager.CreateDefaultPadSetting(xid, PS, "dualshock-3"),
                    SettingsManager.CreateDefaultPadSetting(xid, VirtualControllerType.Xbox, "xbox-360-wired"),
                    SettingsManager.CreateDefaultPadSetting(rawInput, PS, Full),
                })
                    Assert.All(Targets, t => Assert.Equal("", Field(other, t)));
            }
            finally
            {
                (xid.Device as IDisposable)?.Dispose();
                (rawInput.Device as IDisposable)?.Dispose();
            }
        }

        /// <summary>An original Xbox controller as its cached entry keeps it:
        /// no device object, the raw button count folded into the 22 gamepad
        /// positions, the 12 raw axes, and the SDL GUID of the driver that
        /// last opened it.</summary>
        private static UserDevice CachedXid(string signature = "68", int rawAxes = 12, ushort vid = 0x045E, ushort pid = 0x0202)
        {
            var objects = new List<DeviceObjectItem>();
            for (int a = 0; a < rawAxes; a++)
                objects.Add(new DeviceObjectItem { InputIndex = a, ObjectType = DeviceObjectTypeFlags.AbsoluteAxis });
            objects.Add(new DeviceObjectItem { InputIndex = 0, ObjectType = DeviceObjectTypeFlags.PointOfViewController });
            for (int b = 0; b < 11; b++)
                objects.Add(new DeviceObjectItem { InputIndex = b, ObjectType = DeviceObjectTypeFlags.PushButton });
            return new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), VendorId = vid, ProdId = pid,
                SdlGuid = XidGuid(signature, vid, pid), RawAxisCount = rawAxes, RawButtonCount = 22,
                CapType = InputDeviceType.Gamepad,
                DeviceObjects = objects.ToArray(),
            };
        }

        [Fact]
        public void ACachedOriginalXboxControllerMapsItsPressureLikeAConnectedOne()
        {
            var cached = CachedXid();
            Assert.Null(cached.Device);
            Assert.Equal("hidapi", SdlDeviceWrapper.BackendFromGuid(cached.SdlGuid));

            var ps = SettingsManager.CreateDefaultPadSetting(cached, PS, Full);
            Assert.Equal(Enumerable.Range(6, 6).Select(i => $"Axis {i}"), XidTargets.Select(t => Field(ps, t)));
            Assert.All(Targets.Skip(6), t => Assert.Equal("", Field(ps, t)));
            // The rest of the pad maps one to one as before: White, the
            // left shoulder, is L1, and the D-pad is the hat.
            Assert.Equal("Button 0", ps.ButtonA);
            Assert.Equal("Button 4", ps.LeftShoulder);
            Assert.Equal("Axis 2", ps.LeftTrigger);
            Assert.Equal("POV 0 Up", ps.DPadUp);
        }

        [Theory]
        [InlineData("68", 12, 0x045E, 0x0202, true)]   // the XID driver
        [InlineData("72", 12, 0x045E, 0x0202, false)]  // RawInput
        [InlineData("00", 12, 0x045E, 0x0202, false)]  // DirectInput
        [InlineData("68", 6, 0x045E, 0x0202, false)]   // no pressure axes
        [InlineData("68", 9, 0x0A7B, 0xD000, false)]   // the Steel Battalion
        [InlineData("68", 12, 0x045E, 0x028E, false)]  // an Xbox 360 pad
        public void ACachedOriginalXboxControllerAnswersByItsDriverAndShape(string signature, int rawAxes, int vid, int pid, bool expected)
            => Assert.Equal(expected, ButtonPressureSources.AxesFor(CachedXid(signature, rawAxes, (ushort)vid, (ushort)pid)) != null);

        /// <summary>An original Xbox controller shared over Remote Link
        /// answers from the owner's raw counts the link carries, and an old
        /// peer that sends none gets no answer.</summary>
        [Theory]
        [InlineData(11, true)]
        [InlineData(15, true)]
        [InlineData(17, false)]
        [InlineData(0, false)]
        public void ASharedOriginalXboxControllerAnswersFromItsOwnersCounts(int rawButtons, bool answers)
        {
            var info = new PadForge.Engine.RemoteLink.RemotePeerDeviceInfo
            {
                VendorId = 0x045E, ProductId = 0x0202, NumAxes = 6, NumButtons = 22,
                RawAxisCount = 12, RawButtonCount = rawButtons,
            };
            var shared = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), VendorId = 0x045E, ProdId = 0x0202,
                Device = new PadForge.Engine.RemoteLink.RemotePeerDevice(info),
                IsOnline = true, CapType = InputDeviceType.Gamepad,
            };
            var axes = ButtonPressureSources.AxesFor(shared);
            if (answers) Assert.Equal(new[] { 6, 7, 8, 9, 10, 11, -1, -1, -1, -1 }, axes);
            else Assert.Null(axes);
        }

        [Fact]
        public void ChangingToTheFullPresetFillsAnOriginalXboxControllersSixRows()
        {
            var devices = SettingsManager.UserDevices;
            var settings = SettingsManager.UserSettings;
            var cached = CachedXid();
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(cached);
                var ps = new PadSetting { ButtonA = "Button 0" };
                var us = new UserSetting { InstanceGuid = cached.InstanceGuid, MapTo = 0 };
                us.SetPadSetting(ps);
                lock (SettingsManager.UserSettings.SyncRoot) SettingsManager.UserSettings.Items.Add(us);

                Assert.True(DeviceService.FillEmptyPressureMappingsForSlot(0, Full));
                Assert.Equal("Axis 6", ps.PressureButtonA);
                Assert.Equal("Axis 11", ps.PressureRightShoulder);
                Assert.Equal("", ps.PressureDPadUp);
                Assert.Equal("", ps.PressureDPadRight);
            }
            finally
            {
                SettingsManager.UserDevices = devices;
                SettingsManager.UserSettings = settings;
            }
        }

        [Fact]
        public void ChangingToTheFullPresetFillsOnlyThePressureFields()
        {
            var devices = SettingsManager.UserDevices;
            var settings = SettingsManager.UserSettings;
            var ds3 = Ds3();
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ds3);
                // The default map binds Guide, so a cleared Guide is the
                // user's choice the fill must keep.
                Assert.Equal("Button 10", SettingsManager.CreateDefaultPadSetting(ds3, PS, Full).ButtonGuide);
                var ps = new PadSetting { ButtonA = "Button 0", ButtonGuide = "", PressureButtonB = "Axis 3" };
                var us = new UserSetting { InstanceGuid = ds3.InstanceGuid, MapTo = 0 };
                us.SetPadSetting(ps);
                lock (SettingsManager.UserSettings.SyncRoot) SettingsManager.UserSettings.Items.Add(us);

                Assert.True(DeviceService.FillEmptyPressureMappingsForSlot(0, Full));

                Assert.Equal("Axis 6", ps.PressureButtonA);
                Assert.Equal("Axis 3", ps.PressureButtonB);   // the user's own stays
                Assert.Equal("Axis 8", ps.PressureButtonX);
                Assert.Equal("Axis 15", ps.PressureDPadRight);
                Assert.Equal("", ps.ButtonGuide);
                Assert.Equal(ps.ComputeChecksum(), ps.PadSettingChecksum);
                Assert.Equal(ps.PadSettingChecksum, us.PadSettingChecksum);
                // Nothing left to fill, so nothing to merge.
                Assert.False(DeviceService.FillEmptyPressureMappingsForSlot(0, Full));
            }
            finally
            {
                SettingsManager.UserDevices = devices;
                SettingsManager.UserSettings = settings;
                (ds3.Device as IDisposable)?.Dispose();
            }
        }

        [Fact]
        public void PickingTheFullPresetFillsTheSlotsPressureRows()
        {
            var devices = SettingsManager.UserDevices;
            var settings = SettingsManager.UserSettings;
            var sets = SettingsManager.SlotMappingSets;
            var created = SettingsManager.SlotCreated;
            var hook = SettingsService.AfterMappingSetsRefreshed;
            string stamp = SettingsManager.GetWireStamp(0);
            var ds3 = Ds3();
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
                SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
                SettingsManager.SlotCreated[0] = true;
                SettingsService.AfterMappingSetsRefreshed = null;
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ds3);
                var ps = new PadSetting { ButtonA = "Button 0" };
                var us = new UserSetting { InstanceGuid = ds3.InstanceGuid, MapTo = 0 };
                us.SetPadSetting(ps);
                lock (SettingsManager.UserSettings.SyncRoot) SettingsManager.UserSettings.Items.Add(us);

                var vm = new PadViewModel(0) { OutputType = PS };
                // The slot runs a DualShock 4, then the user picks the Full.
                SettingsManager.StampNintendoWire(0, "dualshock-4-v2");
                vm.ProfileId = Full;

                Assert.Equal("Axis 6", ps.PressureButtonA);
                var row = SettingsManager.SlotMappingSets[0]?.Rows.Single(r => r.Target == "PressureButtonA");
                Assert.NotNull(row);
                Assert.Equal("Axis 6", row.Sources.Single().Descriptor);
                Assert.Equal(Targets.Length, vm.Mappings.Count(m => MappingSetMigrator.IsPressureTarget(m.TargetSettingName)));
            }
            finally
            {
                SettingsManager.StampNintendoWire(0, stamp);
                SettingsService.AfterMappingSetsRefreshed = hook;
                SettingsManager.UserDevices = devices;
                SettingsManager.UserSettings = settings;
                SettingsManager.SlotMappingSets = sets;
                SettingsManager.SlotCreated = created;
                (ds3.Device as IDisposable)?.Dispose();
            }
        }

        /// <summary>The same pick on a slot created this session, which has
        /// no stamp of its own until the type step gives it one. The stamp
        /// stayed unknown, the pick read as a restore, and the ten rows came
        /// up empty.</summary>
        [Fact]
        public void PickingTheFullPresetOnASlotCreatedThisSessionFillsItsPressureRows()
        {
            var devices = SettingsManager.UserDevices;
            var settings = SettingsManager.UserSettings;
            var sets = SettingsManager.SlotMappingSets;
            var created = SettingsManager.SlotCreated;
            var hook = SettingsService.AfterMappingSetsRefreshed;
            string stamp = SettingsManager.GetWireStamp(0);
            var ds3 = Ds3();
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
                SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
                SettingsManager.SlotCreated[0] = true;
                SettingsService.AfterMappingSetsRefreshed = null;
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ds3);
                var ps = new PadSetting { ButtonA = "Button 0" };
                var us = new UserSetting { InstanceGuid = ds3.InstanceGuid, MapTo = 0 };
                us.SetPadSetting(ps);
                lock (SettingsManager.UserSettings.SyncRoot) SettingsManager.UserSettings.Items.Add(us);

                // CreateSlot's two steps on a fresh index.
                SettingsManager.StampNintendoWire(0, null);
                var vm = new PadViewModel(0) { OutputType = PS };
                vm.ProfileId = InputManager.GetDefaultProfileId(PS);
                vm.ProfileId = Full;

                Assert.Equal("Axis 6", ps.PressureButtonA);
                Assert.Equal(Targets.Length, vm.Mappings.Count(m => MappingSetMigrator.IsPressureTarget(m.TargetSettingName)));
            }
            finally
            {
                SettingsManager.StampNintendoWire(0, stamp);
                SettingsService.AfterMappingSetsRefreshed = hook;
                SettingsManager.UserDevices = devices;
                SettingsManager.UserSettings = settings;
                SettingsManager.SlotMappingSets = sets;
                SettingsManager.SlotCreated = created;
                (ds3.Device as IDisposable)?.Dispose();
            }
        }

        // ── Storage ──

        [Fact]
        public void TheFieldsBecomePressureRows()
        {
            var ps = new PadSetting { PressureButtonA = "Axis 6", PressureDPadRight = "Axis 15" };
            var ms = MappingSetMigrator.BuildFromLegacy(0,
                new List<(string, PadSetting, bool)> { ("dev", ps, true) });
            var a = ms.Rows.Single(r => r.Target == "PressureButtonA");
            Assert.Equal("Axis 6", a.Sources.Single().Descriptor);
            Assert.Equal("dev", a.Sources.Single().DeviceGuid);
            Assert.Equal("Axis 15", ms.Rows.Single(r => r.Target == "PressureDPadRight").Sources.Single().Descriptor);
            Assert.Equal(2, ms.Rows.Count(r => MappingSetMigrator.IsPressureTarget(r.Target)));
        }

        [Fact]
        public void APressureFieldIsAMappingFieldEverywhere()
        {
            var ps = new PadSetting();
            Assert.False(ps.HasAnyMapping);
            string before = ps.ComputeChecksum();
            ps.PressureDPadLeft = "Axis 14";
            Assert.True(ps.HasAnyMapping);
            Assert.NotEqual(before, ps.ComputeChecksum());
            Assert.Contains("Axis 14", ps.GetAllMappingDescriptors());

            // The save path clears and repopulates every mapping field from
            // the grid, so a row the user cleared stays cleared.
            var saved = new PadSetting { PressureDPadLeft = "Axis 14", PressureButtonY = "Axis 9" };
            saved.ClearMappingDescriptors(new Dictionary<string, string> { ["PressureButtonY"] = "Axis 3" });
            Assert.Equal("", saved.PressureDPadLeft);
            Assert.Equal("Axis 3", saved.PressureButtonY);

            // Xbox and PlayStation share one layout, so a copy carries it.
            var copy = new PadSetting();
            copy.CopyFromTranslated(ps, PS, false, VirtualControllerType.Xbox, false);
            Assert.Equal("Axis 14", copy.PressureDPadLeft);

            // No other layout has pressure: onto a gamepad from one, it clears,
            // and a stray field on the other side does not ride across.
            var target = new PadSetting { PressureDPadLeft = "Axis 3" };
            target.CopyFromTranslated(new PadSetting { PressureDPadLeft = "Axis 9" },
                VirtualControllerType.Extended, true, PS, false);
            Assert.Equal("", target.PressureDPadLeft);
        }
    }
}
