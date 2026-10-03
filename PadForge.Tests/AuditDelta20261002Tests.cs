using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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
    /// <summary>Delta-audit 2026-10-02 contracts that run the engine's
    /// shared per-slot statics.</summary>
    [Collection("SettingsManagerStatics")]
    public class AuditDelta20261002EngineTests
    {
        private static readonly MethodInfo ApplyGamepad = typeof(InputManager).GetMethod(
            "ApplyMappingSetToGamepad", BindingFlags.Static | BindingFlags.NonPublic);

        private static MappingSet LayeredSet(MappingSource baseSource, MappingSource layerSource)
        {
            var ms = new MappingSet();
            ms.Rows.Add(new MappingRow { Target = "ButtonA", LayerMask = "Base", Sources = { baseSource } });
            ms.Rows.Add(new MappingRow { Target = "ButtonA", LayerMask = "View", Sources = { layerSource } });
            ms.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "", Descriptor = "Button 28", Mode = "Hold",
                LayerMask = "View", LayerName = "View", Kind = "Button", DelayMs = 0,
            });
            return ms;
        }

        /// <summary>One poll frame of the gamepad row loop on
        /// <paramref name="slot"/>: true when it writes button A.</summary>
        private static bool PressesA(int slot, MappingSet ms, CustomInputState state)
        {
            InputManager.GetSlotSourceKindRuntime(slot).FrameSeq++;
            var args = new object[] { state, ms, "", 50, slot, new Gamepad() };
            ApplyGamepad.Invoke(null, args);
            return (((Gamepad)args[5]).Buttons & Gamepad.A) != 0;
        }

        // ── A1: a layer's row keeps its own Toggle, Rapid Trigger and flick state ──

        /// <summary>A shift layer's Rapid Trigger row on the same target as a
        /// Base row starts from its own zone. Sharing the Base row's key, it
        /// took over the Base row's deepest point in the frame the layer
        /// engaged: its input at 80 percent read as a 20 percent rise from
        /// the Base row's full press and released, past its own deadzone,
        /// where a Direct row presses.</summary>
        [Fact]
        public void ALayersRapidTriggerRowStartsFromItsOwnZone()
        {
            const int slot = 9;
            InputManager.ClearAllShiftRuntime();
            InputManager.GetSlotSourceKindRuntime(slot).Clear();
            try
            {
                var ms = LayeredSet(
                    new MappingSource { Kind = "RapidTrigger", Descriptor = "Axis 2", DeadZone = 30, ParamRapidTriggerDistance = 10 },
                    new MappingSource { Kind = "RapidTrigger", Descriptor = "Axis 5", DeadZone = 30, ParamRapidTriggerDistance = 10 });
                var state = new CustomInputState();
                state.Axis[2] = 65535;
                Assert.True(PressesA(slot, ms, state));

                state.Buttons[28] = true;
                state.Axis[5] = (int)(0.8 * 65535);
                Assert.Equal("View", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));
                Assert.True(PressesA(slot, ms, state));
            }
            finally
            {
                InputManager.ClearAllShiftRuntime();
                InputManager.GetSlotSourceKindRuntime(slot).Clear();
            }
        }

        /// <summary>A latched Base Toggle does not carry into the layer's
        /// Toggle row on the same target. The latch's contract is that a
        /// frame with no read releases it, and the layer row took it over
        /// latched with no press of its own input.</summary>
        [Fact]
        public void ALatchedBaseToggleDoesNotCarryIntoTheLayersRow()
        {
            const int slot = 10;
            InputManager.ClearAllShiftRuntime();
            InputManager.GetSlotSourceKindRuntime(slot).Clear();
            try
            {
                var ms = LayeredSet(
                    new MappingSource { Kind = "Toggle", Descriptor = "Button 1" },
                    new MappingSource { Kind = "Toggle", Descriptor = "Button 2" });
                var state = new CustomInputState();
                Assert.False(PressesA(slot, ms, state));    // a press already down at the first read never flips it
                state.Buttons[1] = true;
                Assert.True(PressesA(slot, ms, state));
                state.Buttons[1] = false;
                Assert.True(PressesA(slot, ms, state));     // latched

                state.Buttons[28] = true;                   // the layer engages, its input untouched
                Assert.False(PressesA(slot, ms, state));
                state.Buttons[2] = true;
                Assert.True(PressesA(slot, ms, state));     // its own press latches it
            }
            finally
            {
                InputManager.ClearAllShiftRuntime();
                InputManager.GetSlotSourceKindRuntime(slot).Clear();
            }
        }

        /// <summary>The runtime keys all three by the row's layer: the layer
        /// handed in for one row never reaches another layer's state, and an
        /// unset layer is Base.</summary>
        [Fact]
        public void TheRuntimeKeysEachRowsStateByItsLayer()
        {
            var rt = new SourceKindRuntime();
            rt.FrameSeq++;
            Assert.True(rt.TickRapidTrigger(0, "ButtonA", 0, true, 1.0, 0.1, null));
            rt.FrameSeq++;
            Assert.True(rt.TickRapidTrigger(0, "ButtonA", 0, true, 0.8, 0.1, "View"));
            Assert.False(rt.TickRapidTrigger(0, "ButtonA", 0, true, 0.8, 0.1, "Base"));   // Base's own: a 20 % rise releases

            rt.FrameSeq++;
            Assert.Equal(0.0, rt.TickToggle(0, "ButtonB", 0, false, 1.0, "Base"));
            rt.FrameSeq++;
            Assert.Equal(1.0, rt.TickToggle(0, "ButtonB", 0, true, 1.0, "Base"));
            rt.FrameSeq++;
            Assert.Equal(0.0, rt.TickToggle(0, "ButtonB", 0, false, 1.0, "View"));
            Assert.Equal(1.0, rt.TickToggle(0, "ButtonB", 0, false, 1.0, null));

            // A Base row flicking on the right stick hands a layer row on the
            // left stick nothing: the layer's first read re-arms at its own
            // stick's angle, so no rotation is computed across two sticks.
            static CustomInputState Stick(int xAxis, double angleDeg)
            {
                double rad = angleDeg * Math.PI / 180.0;
                var s = new CustomInputState();
                s.Axis[xAxis] = 32768 + (int)Math.Round(-Math.Sin(rad) * 32767);
                s.Axis[xAxis + 1] = 32768 + (int)Math.Round(-Math.Cos(rad) * 32767);
                return s;
            }
            var right = new MappingSource { Descriptor = "Flick Stick Right", ParamFlickCountsPer360 = 14400 };
            var left = new MappingSource { Descriptor = "Flick Stick Left", ParamFlickCountsPer360 = 14400 };
            long seq = 1;
            rt.TickFlickStick(0, "KbmMouseX", 0, right, Stick(3, -90), 0.004, seq++, "Base");
            for (int i = 0; i < 50; i++)
                rt.TickFlickStick(0, "KbmMouseX", 0, right, Stick(3, -90), 0.004, seq++, "Base");
            Assert.Equal(0, rt.TickFlickStick(0, "KbmMouseX", 0, left, Stick(0, 90), 0.004, seq++, "View"));
        }

        [Fact]
        public void EveryRowEvaluatorCallThatTicksStatePassesTheRowsLayer()
        {
            // A source kind's state is keyed by the row's layer only if every
            // call that hands the evaluator the slot's runtime passes the
            // row's layer along.
            string eval = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "InputManager.Step3.MappingSetEval.cs");
            var calls = Regex.Matches(eval, @"SourceEvaluator\.EvaluateFor\w+\([^;]*;")
                .Select(m => m.Value)
                .Where(c => Regex.IsMatch(c, @",\s*(runtime|slotRuntime)\s*,"))
                .ToList();
            Assert.True(calls.Count >= 25, $"only {calls.Count} runtime calls found");
            Assert.All(calls, c => Assert.Contains("layer: row.LayerMask", c));

            string kbm = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "InputManager.Step3.UpdateOutputStates.cs");
            Assert.Matches(@"runtime\.TickFlickStick\([^;]*row\.LayerMask\);", kbm);
        }
    }

    /// <summary>Delta-audit 2026-10-02: a device that drops leaves nothing
    /// of its last frame in a slot another device keeps active.</summary>
    [Collection("SettingsManagerStatics")]
    public class AuditDelta20261002DisconnectTests
    {
        private sealed class Pad : WebControllerDevice, ISdlInputDevice
        {
            public Pad() : base(Guid.NewGuid().ToString(), "Disconnect Pad") { }
            public bool HasGyroAux { get; set; }
            public bool HasAccelAux { get; set; }
        }

        /// <summary>Step 3 and Step 4 of a real InputManager over settings
        /// built by hand (the PressureRowsTests rig).</summary>
        private sealed class Rig : IDisposable
        {
            private readonly SettingsCollection _settings = SettingsManager.UserSettings;
            private readonly DeviceCollection _devices = SettingsManager.UserDevices;
            private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
            private readonly bool[] _created = SettingsManager.SlotCreated;
            private readonly bool[] _enabled = SettingsManager.SlotEnabled;
            private readonly List<Pad> _wrappers = new();
            private readonly Action _step3, _step4;
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

            public void Slot(int slot, VirtualControllerType type, params MappingRow[] rows)
            {
                SettingsManager.SlotCreated[slot] = true;
                SettingsManager.SlotEnabled[slot] = true;
                Manager.SlotControllerTypes[slot] = type;
                var set = new MappingSet();
                set.Rows.AddRange(rows);
                SettingsManager.SlotMappingSets[slot] = set;
            }

            public void Poll()
            {
                _step3();
                _step4();
            }

            /// <summary>The confirmed-disconnect path: the device goes offline,
            /// then MarkDeviceOffline tears it down.</summary>
            public void Drop(UserDevice ud)
            {
                ud.IsOnline = false;
                typeof(InputManager).GetMethod("MarkDeviceOffline", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(Manager, new object[] { ud });
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
                    SettingsManager.UserSettings = _settings;
                    SettingsManager.UserDevices = _devices;
                    SettingsManager.SlotMappingSets = _sets;
                    SettingsManager.SlotCreated = _created;
                    SettingsManager.SlotEnabled = _enabled;
                }
            }
        }

        private static UserSetting Setting(UserDevice d)
            => SettingsManager.UserSettings.Items.First(us => us.InstanceGuid == d.InstanceGuid);

        /// <summary>A controller held a mouse button on a Keyboard + Mouse
        /// slot and dropped. The slot's other device keeps it active, and
        /// Step 4 merged the dropped device's last frame on every poll, so
        /// the button stayed down until the controller came back.</summary>
        [Fact]
        public void ADroppedDeviceReleasesItsMouseButtonOnASlotAnotherDeviceKeepsActive()
        {
            using var rig = new Rig();
            var pad = rig.AddDevice(0);
            var other = rig.AddDevice(0);
            rig.Slot(0, VirtualControllerType.KeyboardMouse, new MappingRow
            {
                Target = "KbmMBtn0", LayerMask = "Base",
                Sources = { new MappingSource { Kind = "Direct", DeviceGuid = pad.InstanceGuidString, Descriptor = "Button 0" } },
            });

            pad.InputState.Buttons[0] = true;
            rig.Poll();
            Assert.True(rig.Manager.CombinedKbmRawStates[0].GetMouseButton(0));

            rig.Drop(pad);
            rig.Poll();
            Assert.True(other.IsOnline);
            Assert.False(rig.Manager.CombinedKbmRawStates[0].GetMouseButton(0));
        }

        /// <summary>The raw HID and MIDI states rest in fresh arrays of the
        /// same shape: published arrays are never written, a trigger rests at
        /// short.MinValue, a hat at -1, a CC at center. The value states go
        /// to their defaults.</summary>
        [Fact]
        public void ADroppedDevicesRawStatesRestInFreshArraysOfTheSameShape()
        {
            using var rig = new Rig();
            var pad = rig.AddDevice(0);
            rig.Manager.SlotCustomLayouts[0] = new CustomControllerLayout { Axes = 6, Buttons = 16, Povs = 1, Sticks = 2, Triggers = 2 };
            var us = Setting(pad);
            var liveRaw = new RawHidState
            {
                Axes = new short[] { 1000, -2000, 5000, 300, -300, 7000 },
                HardwareAxes = new short[] { 1100, -2100, 5100, 310, -310, 7100 },
                Buttons = new uint[] { 0x5 },
                Povs = new[] { 9000 },
            };
            var liveMidi = new MidiRawState { CcValues = new byte[] { 127, 0 }, Notes = new[] { true, false, true } };
            us.RawHidOutputState = liveRaw;
            us.MidiRawOutputState = liveMidi;
            var kbm = new KbmRawState { MouseGyroX = 3f };
            kbm.SetMouseButton(0, true);
            us.KbmRawOutputState = kbm;
            var vr = new VrRawState();
            vr.Left.Buttons = 1;
            us.VrRawOutputState = vr;
            us.TouchpadOutputState = new TouchpadState { Down0 = true, X0 = 0.4f };

            rig.Drop(pad);

            var raw = us.RawHidOutputState;
            Assert.NotSame(liveRaw.Axes, raw.Axes);
            Assert.NotSame(liveRaw.Buttons, raw.Buttons);
            Assert.Equal(new short[] { 0, 0, short.MinValue, 0, 0, short.MinValue }, raw.Axes);
            Assert.Equal(new short[] { 0, 0, short.MinValue, 0, 0, short.MinValue }, raw.HardwareAxes);
            Assert.Equal(new uint[] { 0 }, raw.Buttons);
            Assert.Equal(new[] { -1 }, raw.Povs);
            Assert.Equal(0x5u, liveRaw.Buttons[0]);           // the published arrays are untouched

            var midi = us.MidiRawOutputState;
            Assert.NotSame(liveMidi.CcValues, midi.CcValues);
            Assert.Equal(new byte[] { 64, 64 }, midi.CcValues);
            Assert.Equal(new[] { false, false, false }, midi.Notes);
            Assert.True(liveMidi.Notes[0]);

            Assert.Equal(default, us.KbmRawOutputState);
            Assert.Equal(default, us.VrRawOutputState);
            Assert.Equal(default, us.TouchpadOutputState);
        }

        /// <summary>Step 3 rests the states of a setting whose device is no
        /// longer listed, and a state already at rest is not published again,
        /// so the per-frame pass allocates once.</summary>
        [Fact]
        public void ARemovedDevicesStatesRestOnceAndStayPublished()
        {
            using var rig = new Rig();
            rig.Slot(0, VirtualControllerType.Midi);
            var gone = new UserSetting { InstanceGuid = Guid.NewGuid(), MapTo = 0 };
            gone.SetPadSetting(new PadSetting());
            gone.MidiRawOutputState = new MidiRawState { CcValues = new byte[] { 100 }, Notes = new[] { true } };
            gone.KbmRawOutputState = new KbmRawState { MouseGyroY = 2f };
            SettingsManager.UserSettings.Items.Add(gone);

            rig.Poll();
            var first = gone.MidiRawOutputState;
            Assert.Equal(new byte[] { 64 }, first.CcValues);
            Assert.Equal(new[] { false }, first.Notes);
            Assert.Equal(default, gone.KbmRawOutputState);

            rig.Poll();
            Assert.Same(first.CcValues, gone.MidiRawOutputState.CcValues);
            Assert.Same(first.Notes, gone.MidiRawOutputState.Notes);
        }
    }


    /// <summary>Delta-audit 2026-10-02: every slot's wire stamp is owned
    /// from its first profile.</summary>
    [Collection("SettingsManagerStatics")]
    public class AuditDelta20261002WireStampTests
    {
        /// <summary>A profile apply or a slot compaction installs the incoming
        /// sets, then sets the type, then stamps its own profile. The type
        /// step ran the default profile as a live re-target from the
        /// outgoing stamp: an Xbox slot turned Nintendo translated the
        /// incoming Switch 2 Pro set from the Xbox wire and pruned every row
        /// past the original's fourteen buttons. The type step now stamps
        /// the new category's default first and moves nothing.</summary>
        [Fact]
        public void ATypeChangeLeavesTheIncomingSetAlone()
        {
            var settings = SettingsManager.UserSettings;
            var devices = SettingsManager.UserDevices;
            var sets = SettingsManager.SlotMappingSets;
            var created = SettingsManager.SlotCreated;
            var hook = SettingsService.AfterMappingSetsRefreshed;
            string stamp = SettingsManager.GetWireStamp(0);
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
                SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
                SettingsManager.SlotCreated[0] = true;
                SettingsService.AfterMappingSetsRefreshed = null;
                var vm = new PadViewModel(0);
                SettingsManager.StampNintendoWire(0, InputManager.GetDefaultProfileId(VirtualControllerType.Xbox));
                var set = new MappingSet();
                set.Rows.Add(new MappingRow
                {
                    Target = "RawBtn15", LayerMask = "Base",
                    Sources = { new MappingSource { Kind = "Direct", Descriptor = "Button 3" } },
                });
                SettingsManager.SlotMappingSets[0] = set;

                vm.OutputType = VirtualControllerType.Nintendo;

                Assert.Equal(InputManager.GetDefaultProfileId(VirtualControllerType.Nintendo), SettingsManager.GetWireStamp(0));
                var row = Assert.Single(SettingsManager.SlotMappingSets[0].Rows);
                Assert.Equal("RawBtn15", row.Target);
                Assert.Equal("Button 3", Assert.Single(row.Sources).Descriptor);
            }
            finally
            {
                SettingsManager.StampNintendoWire(0, stamp);
                SettingsService.AfterMappingSetsRefreshed = hook;
                SettingsManager.UserSettings = settings;
                SettingsManager.UserDevices = devices;
                SettingsManager.SlotMappingSets = sets;
                SettingsManager.SlotCreated = created;
            }
        }

        /// <summary>A profile change on a slot whose stamp is unknown adopts
        /// the new profile and moves nothing, so the next change on the slot
        /// reads as the live change it is.</summary>
        [Fact]
        public void AProfileChangeOnAnUnknownStampAdoptsTheProfile()
        {
            var settings = SettingsManager.UserSettings;
            var devices = SettingsManager.UserDevices;
            var sets = SettingsManager.SlotMappingSets;
            var created = SettingsManager.SlotCreated;
            var hook = SettingsService.AfterMappingSetsRefreshed;
            string stamp = SettingsManager.GetWireStamp(0);
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
                SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
                SettingsService.AfterMappingSetsRefreshed = null;
                var vm = new PadViewModel(0) { OutputType = VirtualControllerType.Nintendo };
                SettingsManager.StampNintendoWire(0, null);

                vm.ProfileId = "switch2-pro-controller";

                Assert.Equal("switch2-pro-controller", SettingsManager.GetWireStamp(0));
            }
            finally
            {
                SettingsManager.StampNintendoWire(0, stamp);
                SettingsService.AfterMappingSetsRefreshed = hook;
                SettingsManager.UserSettings = settings;
                SettingsManager.UserDevices = devices;
                SettingsManager.SlotMappingSets = sets;
                SettingsManager.SlotCreated = created;
            }
        }

        /// <summary>Delete clears the slot's stamp, and create stamps the new
        /// slot's profile even when the pad already carried its type and
        /// profile, where neither setter runs. Left unknown, the first live
        /// preset change on the slot read as a restore.</summary>
        [Fact]
        public void DeleteClearsTheStampAndCreateStampsTheNewSlot()
        {
            var settings = SettingsManager.UserSettings;
            var devices = SettingsManager.UserDevices;
            var sets = SettingsManager.SlotMappingSets;
            var profiles = SettingsManager.Profiles;
            var created = SettingsManager.SlotCreated;
            var enabled = SettingsManager.SlotEnabled;
            var hook = SettingsService.AfterMappingSetsRefreshed;
            var orders = Enum.GetValues<VirtualControllerType>()
                .ToDictionary(t => t, t => SettingsManager.SlotOrders.GetOrderFor(t).ToArray());
            var stamps = Enumerable.Range(0, InputManager.MaxPads).Select(SettingsManager.GetWireStamp).ToArray();
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
                SettingsManager.Profiles = new List<ProfileData>();
                SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
                SettingsManager.SlotEnabled = new bool[InputManager.MaxPads];
                foreach (var type in orders.Keys) SettingsManager.SlotOrders.GetOrderFor(type).Clear();
                for (int i = 0; i < InputManager.MaxPads; i++) SettingsManager.StampNintendoWire(i, null);
                var vm = new MainViewModel();
                var devicesService = new DeviceService(vm, new SettingsService(vm));
                string ps = InputManager.GetDefaultProfileId(VirtualControllerType.PlayStation);

                int slot = devicesService.CreateSlot(VirtualControllerType.PlayStation);
                Assert.Equal(ps, SettingsManager.GetWireStamp(slot));

                devicesService.DeleteSlot(slot);
                Assert.Null(SettingsManager.GetWireStamp(slot));

                Assert.Equal(slot, devicesService.CreateSlot(VirtualControllerType.PlayStation));
                Assert.Equal(ps, vm.Pads[slot].ProfileId);
                Assert.Equal(ps, SettingsManager.GetWireStamp(slot));
            }
            finally
            {
                for (int i = 0; i < InputManager.MaxPads; i++) SettingsManager.StampNintendoWire(i, stamps[i]);
                SettingsService.AfterMappingSetsRefreshed = hook;
                SettingsManager.UserSettings = settings;
                SettingsManager.UserDevices = devices;
                SettingsManager.SlotMappingSets = sets;
                SettingsManager.Profiles = profiles;
                SettingsManager.SlotCreated = created;
                SettingsManager.SlotEnabled = enabled;
                foreach (var pair in orders)
                {
                    var order = SettingsManager.SlotOrders.GetOrderFor(pair.Key);
                    order.Clear();
                    order.AddRange(pair.Value);
                }
            }
        }
    }

    /// <summary>Delta-audit 2026-10-02: a live PlayStation preset change fills
    /// what the new preset's default maps and the outgoing one's does
    /// not.</summary>
    [Collection("SettingsManagerStatics")]
    public class AuditDelta20261002PresetFillTests
    {
        private const VirtualControllerType PS = VirtualControllerType.PlayStation;

        private static DeviceObjectItem Button(int idx) => new()
        {
            InputIndex = idx,
            ObjectType = DeviceObjectTypeFlags.PushButton,
        };

        /// <summary>A DualSense Edge on a slot: touchpad, Misc 1 at 11 and the
        /// paddles at 12 to 15.</summary>
        private static UserDevice Edge() => new()
        {
            InstanceGuid = Guid.NewGuid(), CapType = InputDeviceType.Gamepad, HasTouchpad = true,
            IsOnline = true, DeviceObjects = Enumerable.Range(0, 17).Select(Button).ToArray(),
        };

        /// <summary>Assigns <paramref name="pad"/> to slot 0 on
        /// <paramref name="preset"/>, the way an assign builds its default,
        /// runs <paramref name="act"/> with the slot's pad view model, and
        /// puts every static back.</summary>
        private static void OnSlot(UserDevice pad, string preset, Action<PadViewModel, PadSetting> act)
        {
            var devices = SettingsManager.UserDevices;
            var settings = SettingsManager.UserSettings;
            var sets = SettingsManager.SlotMappingSets;
            var created = SettingsManager.SlotCreated;
            var hook = SettingsService.AfterMappingSetsRefreshed;
            string stamp = SettingsManager.GetWireStamp(0);
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
                SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
                SettingsManager.SlotCreated[0] = true;
                SettingsService.AfterMappingSetsRefreshed = null;
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(pad);
                var ps = SettingsManager.CreateDefaultPadSetting(pad, PS, preset);
                var us = new UserSetting { InstanceGuid = pad.InstanceGuid, MapTo = 0 };
                us.SetPadSetting(ps);
                lock (SettingsManager.UserSettings.SyncRoot) SettingsManager.UserSettings.Items.Add(us);
                var vm = new PadViewModel(0) { OutputType = PS };
                SettingsManager.StampNintendoWire(0, preset);
                vm.ProfileId = preset;
                act(vm, ps);
            }
            finally
            {
                SettingsManager.StampNintendoWire(0, stamp);
                SettingsService.AfterMappingSetsRefreshed = hook;
                SettingsManager.UserDevices = devices;
                SettingsManager.UserSettings = settings;
                SettingsManager.SlotMappingSets = sets;
                SettingsManager.SlotCreated = created;
            }
        }

        /// <summary>A pad assigned on a DualShock 3 preset gets no touchpad
        /// or Mic Mute binding, since that wire has neither. A switch to the
        /// DualSense filled only pressure, so the touchpad stayed unmapped
        /// and the virtual touchpad never moved.</summary>
        [Fact]
        public void ASwitchFromADualShock3FillsTheTouchpadAndMicRows()
        {
            OnSlot(Edge(), "dualshock-3", (vm, ps) =>
            {
                Assert.True(string.IsNullOrEmpty(ps.TouchpadX1));
                vm.ProfileId = "dualsense-composite";

                Assert.Equal("Touchpad 0 Finger 0 X", ps.TouchpadX1);
                Assert.Equal("Touchpad 0 Finger 1 Down", ps.TouchpadContact2);
                Assert.Equal("Touchpad 0 Click", ps.TouchpadClick);
                Assert.Equal("Button 11", ps.ButtonMute);
                Assert.True(string.IsNullOrEmpty(ps.LeftPaddle));   // the plain DualSense has none
                var row = Assert.Single(SettingsManager.SlotMappingSets[0].Rows, r => r.Target == "TouchpadX1");
                Assert.Equal("Touchpad 0 Finger 0 X", Assert.Single(row.Sources).Descriptor);
            });
        }

        /// <summary>A field the outgoing preset's default mapped and the user
        /// cleared on that wire stays cleared. The Edge's own rows fill.</summary>
        [Fact]
        public void AFieldClearedOnTheOutgoingWireStaysCleared()
        {
            OnSlot(Edge(), "dualsense-composite", (vm, ps) =>
            {
                ps.TouchpadX1 = "";
                ps.ButtonMute = "";
                vm.ProfileId = "dualsense-edge-composite";

                Assert.True(string.IsNullOrEmpty(ps.TouchpadX1));
                Assert.True(string.IsNullOrEmpty(ps.ButtonMute));
                Assert.Equal("Button 12", ps.RightPaddle);
                Assert.Equal("Button 13", ps.LeftPaddle);
                Assert.Equal("Button 14", ps.RightFunction);
                Assert.Equal("Button 15", ps.LeftFunction);
            });
        }

        /// <summary>The fill list is every PlayStation field whose default
        /// depends on the preset: for a pad with every gated capability, any
        /// field two presets' defaults disagree on is on the list.</summary>
        [Fact]
        public void TheFillListCoversEveryPresetGatedField()
        {
            var w = new SdlDeviceWrapper();
            void Set(string name, object value) => typeof(SdlDeviceWrapper).GetProperty(name).SetValue(w, value);
            Set(nameof(SdlDeviceWrapper.VendorId), (ushort)0x054C);
            Set(nameof(SdlDeviceWrapper.ProductId), (ushort)0x0268);
            Set(nameof(SdlDeviceWrapper.RawAxisCount), 16);
            Set(nameof(SdlDeviceWrapper.RawButtonCount), 11);
            var pad = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), VendorId = 0x054C, ProdId = 0x0268, Device = w,
                CapType = InputDeviceType.Gamepad, HasTouchpad = true, HasGyro = true, HasAccel = true,
                DeviceObjects = Enumerable.Range(0, 18).Select(Button).ToArray(),
            };
            try
            {
                var presets = HMaestroProfileCatalog.PlayStationProfiles.Select(p => p.Id).ToList();
                Assert.Contains("dualshock-3-full", presets);
                var defaults = presets.Select(p => SettingsManager.CreateDefaultPadSetting(pad, PS, p)).ToList();
                var gated = typeof(PadSetting).GetProperties()
                    .Where(p => p.PropertyType == typeof(string) && p.CanRead && p.CanWrite
                        && p.Name != nameof(PadSetting.PadSettingChecksum)
                        && defaults.Select(d => p.GetValue(d) as string ?? "").Distinct().Count() > 1)
                    .Select(p => p.Name)
                    .ToList();
                Assert.Contains(nameof(PadSetting.TouchpadX1), gated);
                Assert.Contains(nameof(PadSetting.PressureButtonA), gated);
                Assert.All(gated, name => Assert.Contains(name, DeviceService.PresetGatedPlayStationTargets));
            }
            finally { w.Dispose(); }
        }
    }

    /// <summary>Delta-audit 2026-10-02: game rumble on the DualShock 3
    /// (SIXAXIS): Full preset.</summary>
    public class AuditDelta20261002Ds3RumbleTests
    {
        private static System.Collections.Generic.Dictionary<string, object> Fields(byte rightOn, byte leftForce) => new()
        {
            ["rightMotorDuration"] = (byte)0xFF, ["rightMotorOn"] = rightOn,
            ["leftMotorDuration"] = (byte)0xFF, ["leftMotorForce"] = leftForce,
            ["ledBitmap"] = (byte)0x02,
        };

        /// <summary>The large motor takes the force byte and the small one is
        /// on or off, as hid-sony and SDL's PS3 driver write them. SDL's own
        /// probe of the virtual pad, SDL_RumbleGamepad(0xC800, 0xFFFF),
        /// decodes as force 200 and on.</summary>
        [Fact]
        public void TheLargeMotorTakesTheForceAndTheSmallOneIsOnOrOff()
        {
            Assert.True(HMaestroVirtualController.TryDualShock3Motors(Fields(1, 200), 49, 49, out ushort large, out ushort small));
            Assert.Equal(200 * 257, large);
            Assert.Equal(ushort.MaxValue, small);

            Assert.True(HMaestroVirtualController.TryDualShock3Motors(Fields(0, 0), 49, 49, out large, out small));
            Assert.Equal(0, large);
            Assert.Equal(0, small);
        }

        /// <summary>A short frame or another profile's fields set
        /// nothing.</summary>
        [Fact]
        public void AShortFrameOrAnotherProfilesFieldsSetNothing()
        {
            Assert.False(HMaestroVirtualController.TryDualShock3Motors(Fields(1, 200), 36, 49, out _, out _));
            Assert.False(HMaestroVirtualController.TryDualShock3Motors(Fields(1, 200), 49, -1, out _, out _));
            var sony = new System.Collections.Generic.Dictionary<string, object> { ["leftMotor"] = (byte)200, ["rightMotor"] = (byte)255 };
            Assert.False(HMaestroVirtualController.TryDualShock3Motors(sony, 49, 49, out _, out _));
            Assert.False(HMaestroVirtualController.TryDualShock3Motors(null, 49, 49, out _, out _));
        }

        /// <summary>The bundled profile decodes the two motor bytes where
        /// hid-sony's struct sixaxis_output_report puts them: right_motor_on
        /// at byte 3 and left_motor_force at byte 5, after the report id and
        /// the padding byte.</summary>
        [Fact]
        public void TheFullProfileDecodesBothMotorBytes()
        {
            var spec = HMaestroProfileCatalog.GetProfileById("dualshock-3-full")?.ExtendedOutputReport;
            Assert.NotNull(spec);
            Assert.Contains(spec.Fields, f => f.Semantic == "rightMotorOn" && f.Byte == 3);
            Assert.Contains(spec.Fields, f => f.Semantic == "leftMotorForce" && f.Byte == 5);
            Assert.True(spec.Size > 5);
        }
    }

    /// <summary>Delta-audit 2026-10-02: the six gyro flags read one way in
    /// the view, the slot summary and the engine.</summary>
    public class AuditDelta20261002GyroFlagTests
    {
        private static readonly string[] Flags =
        {
            "GyroInvertPitch", "GyroInvertYaw", "GyroInvertRollEffective",
            "GyroApplyTuningToPassthrough", "GyroCompassYaw", "GyroSimulation",
        };

        /// <summary>A hand-edited "true" ran in the engine while the box
        /// showed unchecked and the summary printed nothing, and a stored "2"
        /// printed SIM while nothing simulated.</summary>
        [Theory]
        [InlineData("1", true)]
        [InlineData("true", true)]
        [InlineData("True", true)]
        [InlineData("0", false)]
        [InlineData("2", false)]
        [InlineData("1.0", false)]
        [InlineData("", false)]
        public void TheViewTheSummaryAndTheEngineReadAFlagAlike(string stored, bool on)
        {
            var ps = new PadSetting
            {
                GyroInvertPitch = stored, GyroInvertYaw = stored, GyroInvertRoll = stored,
                GyroApplyTuningToPassthrough = stored, GyroCompassYaw = stored, GyroSimulation = stored,
            };
            Assert.Equal(on, PadForge.Services.InputService.TryParseBoolPs(stored, false));

            var vm = new PadViewModel(0);
            typeof(PadForge.Services.InputService).GetMethod("LoadPadSettingIntoViewModelCore",
                BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { vm, ps });
            Assert.Equal(on, vm.GyroInvertPitch);
            Assert.Equal(on, vm.GyroInvertYaw);
            Assert.Equal(on, vm.GyroInvertRoll);
            Assert.Equal(on, vm.GyroApplyTuningToPassthrough);
            Assert.Equal(on, vm.GyroCompassYaw);
            Assert.Equal(on, vm.GyroSimulation);

            var parts = new List<string>();
            typeof(PadForge.Services.InputService).GetMethod("AppendGyroStageTokens",
                BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { parts, ps, true });
            Assert.Equal(on, parts.Contains("INV P"));
            Assert.Equal(on, parts.Contains("INV Y"));
            Assert.Equal(on, parts.Contains("INV R"));
            Assert.Equal(on, parts.Contains("PASSTHRU"));
            Assert.Equal(on, parts.Any(p => p.StartsWith("SIM", StringComparison.Ordinal)));
        }

        /// <summary>No reader of the six compares the stored string with "1"
        /// or reads it as a number, in either loader or the summary.</summary>
        [Fact]
        public void NoReaderParsesAGyroFlagItsOwnWay()
        {
            foreach (var file in new[] { "InputService.cs", "SettingsService.cs" })
            {
                string src = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", file);
                foreach (var flag in Flags)
                {
                    Assert.DoesNotContain($"ps.{flag} == \"1\"", src);
                    Assert.DoesNotContain($"PsFlagSet(ps.{flag})", src);
                }
            }
        }
    }

    /// <summary>Delta-audit 2026-10-02: the PlayStation Back and Start rows
    /// carry the names the macro, menu and SOCD lists give them, in every
    /// language.</summary>
    [Collection("CultureSwitching")]
    public class AuditDelta20261002PlayStationNameTests
    {
        private static string Label(PadViewModel vm, string target)
            => vm.Mappings.Single(m => m.TargetSettingName == target).TargetLabel;

        private static string SocdLabel(PadViewModel vm, string target)
            => vm.SocdButtonOptions.Single(o => o.Value == target).Display;

        /// <summary>The grid named these four buttons with English literals,
        /// so in Spanish its DualShock 3 rows read "Select" and "Start" while
        /// the macro, menu and SOCD lists read "Seleccionar" and "Inicio",
        /// and a DualSense's rows read "Share" and "Options" against
        /// "Compartir" and "Opciones".</summary>
        [Theory]
        [InlineData("dualshock-3", true)]
        [InlineData("dualsense-composite", false)]
        public void TheRowsNameBackAndStartAsTheOtherListsDo(string preset, bool ds3)
        {
            var before = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(System.Globalization.CultureInfo.GetCultureInfo("es"));
                var vm = RestoredPad.Build(0, VirtualControllerType.PlayStation, preset);
                var style = ds3 ? MacroButtonStyle.DualShock3 : MacroButtonStyle.DualShock4;

                string back = Label(vm, "ButtonBack");
                string start = Label(vm, "ButtonStart");
                Assert.Equal(ds3 ? Strings.Instance.DevObj_Select : Strings.Instance.Btn_Share, back);
                Assert.Equal(ds3 ? Strings.Instance.Btn_Start : Strings.Instance.Btn_Options, start);
                Assert.NotEqual(ds3 ? "Select" : "Share", back);
                Assert.Equal(back, MacroOutputChannelNames.DisplayName(MacroOutputChannel.Back, style));
                Assert.Equal(start, MacroOutputChannelNames.DisplayName(MacroOutputChannel.Start, style));
                Assert.Equal(back, SocdLabel(vm, "ButtonBack"));
                Assert.Equal(start, SocdLabel(vm, "ButtonStart"));
                var defs = MacroButtonNames.GetButtonDefs(style);
                Assert.Contains(defs, d => d.Label == back && d.Flag == 0x0020);
                Assert.Contains(defs, d => d.Label == start && d.Flag == 0x0010);
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
        }

        /// <summary>The Xbox Series Share row reads the localized name its
        /// neighbors do.</summary>
        [Fact]
        public void TheXboxSeriesShareRowIsLocalized()
        {
            var before = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(System.Globalization.CultureInfo.GetCultureInfo("es"));
                var vm = RestoredPad.Build(0, VirtualControllerType.Xbox, "xbox-series-xs-bt");
                Assert.Equal(Strings.Instance.Btn_Share, Label(vm, "ButtonShare"));
                Assert.NotEqual("Share", Label(vm, "ButtonShare"));
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
        }
    }

    /// <summary>Delta-audit 2026-10-02: a NaN never reaches a persisted
    /// number through a row, a settings object or a mapping source.</summary>
    public class AuditDelta20261002FiniteSettingTests
    {
        /// <summary>The Audio tab's EQ Preamp and crossfeed Feed took a typed
        /// "NaN" through Math.Clamp, and the DSP multiplied every sample by
        /// it. Their class now refuses NaN the way the view models do, and
        /// infinity still clamps.</summary>
        [Fact]
        public void TheAudioTabRefusesANaN()
        {
            var config = new DeviceSlotConfig();
            config.AudioEqPreampDb = 3;
            config.AudioEqPreampDb = double.NaN;
            Assert.Equal(3, config.AudioEqPreampDb);
            config.AudioCrossfeedFeedDb = 6;
            config.AudioCrossfeedFeedDb = double.NaN;
            Assert.Equal(6, config.AudioCrossfeedFeedDb);
            config.AudioEqPreampDb = double.PositiveInfinity;
            Assert.Equal(12, config.AudioEqPreampDb);
        }

        /// <summary>The Incremental rate, minimum and maximum have no clamp, so
        /// they refuse infinity as well as NaN.</summary>
        [Fact]
        public void TheUnclampedSourceNumbersTakeFiniteValuesOnly()
        {
            var source = new MappingSourceItem();
            source.ParamMin = 0.2;
            source.ParamMax = 0.8;
            source.ParamRate = 0.4;
            foreach (double bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                source.ParamMin = bad;
                source.ParamMax = bad;
                source.ParamRate = bad;
            }
            Assert.Equal(0.2, source.ParamMin);
            Assert.Equal(0.8, source.ParamMax);
            Assert.Equal(0.4, source.ParamRate);
            source.ParamReverseMultiplier = double.NaN;
            Assert.Equal(4.0, source.ParamReverseMultiplier);
        }

        /// <summary>Every double on a mapping source keeps its value when
        /// handed NaN or infinity, so a hand-edited "NaN" in the settings
        /// file, which reaches the engine without passing a view model, never
        /// reaches it.</summary>
        [Fact]
        public void EveryMappingSourceDoubleRefusesANonFiniteValue()
        {
            var doubles = typeof(MappingSource).GetProperties()
                .Where(p => p.PropertyType == typeof(double) && p.CanWrite).ToList();
            Assert.Equal(34, doubles.Count);
            foreach (var p in doubles)
            {
                var fresh = new MappingSource();
                double before = (double)p.GetValue(fresh);
                p.SetValue(fresh, double.NaN);
                Assert.Equal(before, (double)p.GetValue(fresh));
                p.SetValue(fresh, double.PositiveInfinity);
                Assert.Equal(before, (double)p.GetValue(fresh));
            }

            var xml = new System.Xml.Serialization.XmlSerializer(typeof(MappingSource));
            using var reader = new StringReader("<MappingSource ParamMin=\"NaN\" ParamMax=\"INF\" ParamRate=\"0.25\" />");
            var loaded = (MappingSource)xml.Deserialize(reader);
            Assert.Equal(0, loaded.ParamMin);
            Assert.Equal(1, loaded.ParamMax);
            Assert.Equal(0.25, loaded.ParamRate);
        }

        /// <summary>Every class that stores a number from a box takes the
        /// NaN-refusing base. The classes left on the plain ObservableObject
        /// carry display values only, which no box writes: live readings and
        /// meters, the on-screen keyboard's key geometry, and a Workshop
        /// row's vote bar.</summary>
        [Fact]
        public void EveryClassWithANumberToStoreRefusesNaN()
        {
            var displayOnly = new[]
            {
                "AxisDisplayItem", "AnalogKeyDisplayItem", "RumbleAudioVoiceItem",
                "KeyboardKeyItem", "WorkshopConfigItem",
            };
            var plain = typeof(ViewModelBase).Assembly.GetTypes()
                .Where(t => typeof(CommunityToolkit.Mvvm.ComponentModel.ObservableObject).IsAssignableFrom(t)
                    && !typeof(FiniteObservableObject).IsAssignableFrom(t)
                    && t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                        .Any(p => (p.PropertyType == typeof(double) || p.PropertyType == typeof(float))
                            && p.SetMethod is { IsPublic: true }))
                .Select(t => t.Name)
                .OrderBy(n => n)
                .ToList();
            Assert.Equal(displayOnly.OrderBy(n => n), plain);
        }
    }

    /// <summary>Delta-audit 2026-10-02: a DualShock 3 calibration names the
    /// state DsHidMini serves the yaw in.</summary>
    [Collection("SettingsManagerStatics")]
    public class AuditDelta20261002Ds3OwnerTests
    {
        /// <summary>Within one DsHidMini release the yaw is zeroed when the
        /// driver holds the pad's EEPROM calibration and unzeroed when it
        /// holds none, so the two states are two owners. A 2.7 rad/s bias
        /// taken unzeroed applied to the zeroed yaw after one USB connection
        /// cached the calibration.</summary>
        [Fact]
        public void TheOwnerNamesDsHidMinisCalibrationState()
        {
            const string Pad = "001fe2a1b2c3";
            Assert.Equal(Pad + "/node:3.15.0.0", Ds3UnitIdentity.NodeOwner(Pad, "3.15.0.0", 0));
            Assert.Equal(Pad + "/node:3.15.0.0/cal", Ds3UnitIdentity.NodeOwner(Pad, "3.15.0.0", 1));
            Assert.Equal(Pad + "/node:3.15.0.0/cal", Ds3UnitIdentity.NodeOwner(Pad, " 3.15.0.0 ", 2));
            Assert.Equal(Pad + "/node:?", Ds3UnitIdentity.NodeOwner(Pad, null, 0));
        }

        /// <summary>DsHidMini writes a Bluetooth connection's calibration
        /// source a second after the link starts, and the property persists,
        /// so the arrival read can still show the last connection's. The node
        /// is read once more after that write, and the new state replaces
        /// the arrival read.</summary>
        [Fact]
        public async Task TheNodeIsReadAgainAfterDsHidMiniWritesTheSource()
        {
            Ds3UnitIdentity.ResetForTests();
            string served = "001fe2a1b2c3/node:3.20.1.0";
            int reads = 0;
            Ds3UnitIdentity.NodeReader = _ => { System.Threading.Interlocked.Increment(ref reads); return served; };
            Ds3UnitIdentity.SettleDelayMs = 50;
            var device = new WebControllerDevice(Guid.NewGuid().ToString(), "Motion Input");
            try
            {
                var ud = new UserDevice
                {
                    InstanceGuid = Guid.NewGuid(), VendorId = 0x054C, ProdId = 0x0268,
                    Device = device, DevicePath = @"\\?\hid#vid_054c&pid_0268#a",
                };
                Ds3UnitIdentity.Prime(ud);
                Assert.Equal("001fe2a1b2c3/node:3.20.1.0", Ds3UnitIdentity.Identity(ud));

                served = "001fe2a1b2c3/node:3.20.1.0/cal";
                for (int i = 0; i < 200 && Ds3UnitIdentity.Identity(ud) != served; i++) await Task.Delay(10);
                Assert.Equal(served, Ds3UnitIdentity.Identity(ud));
                await Task.Delay(200);
                Assert.Equal(2, reads);   // once at arrival, once after the write
            }
            finally
            {
                Ds3UnitIdentity.ResetForTests();
                device.Dispose();
            }
        }
    }

    /// <summary>Delta-audit 2026-10-02: a DualShock 3 shared over Remote Link
    /// carries its owner's identity.</summary>
    [Collection("SettingsManagerStatics")]
    public class AuditDelta20261002SharedDs3Tests
    {
        private static PadForge.Engine.RemoteLink.RemotePeerDeviceInfo Ds3(string id, string identity) => new()
        {
            Slot = 0, PeerLocalDeviceId = id, Name = "PLAYSTATION(R)3 Controller",
            VendorId = 0x054C, ProductId = 0x0268, NumAxes = 6, NumButtons = 22,
            RawButtonCount = 11, RawAxisCount = 16, HasGyro = true,
            InputDeviceType = InputDeviceType.Gamepad, Ds3Identity = identity,
        };

        /// <summary>The identity crosses the wire in the v11 tail, an older
        /// sender's list carries none, and a malformed tail costs only the
        /// identities.</summary>
        [Fact]
        public void TheOwnersDs3IdentityCrossesTheWire()
        {
            var pad = Ds3("ds3", "001fe2a1b2c3/direct");
            var other = Ds3("other", null);
            var payload = PadForge.Engine.RemoteLink.LinkConnection.EncodeDeviceList(new[] { other, pad }, "");
            var list = PadForge.Engine.RemoteLink.LinkConnection.DecodeDeviceList(payload);
            Assert.Null(list[0].Ds3Identity);
            Assert.Equal("001fe2a1b2c3/direct", list[1].Ds3Identity);

            // The tail is the last thing on the wire: an older sender stops
            // before it.
            int start = Array.LastIndexOf(payload, (byte)0xEC);
            Assert.True(start > 0);
            var older = PadForge.Engine.RemoteLink.LinkConnection.DecodeDeviceList(payload.Take(start).ToArray());
            Assert.All(older, d => Assert.Null(d.Ds3Identity));
            Assert.Equal("PLAYSTATION(R)3 Controller", older[1].Name);

            // A record naming a row past the list is refused whole.
            var broken = (byte[])payload.Clone();
            broken[start + 2] = 9;
            var refused = PadForge.Engine.RemoteLink.LinkConnection.DecodeDeviceList(broken);
            Assert.All(refused, d => Assert.Null(d.Ds3Identity));
            Assert.Equal(2, refused.Count);
        }

        /// <summary>A list too large for an older peer still carries the
        /// identity to a peer that reads full lists, since only the
        /// connect-time list can reach an older one, and that list keeps the
        /// older budget.</summary>
        [Fact]
        public void ALargeListCarriesTheIdentityToAPeerThatReadsFullLists()
        {
            var devices = new List<PadForge.Engine.RemoteLink.RemotePeerDeviceInfo>();
            for (int i = 0; i < 120; i++)
            {
                var other = Ds3("pad" + i, null);
                other.SerialNumber = new string('s', 40);
                devices.Add(other);
            }
            devices.Add(Ds3("ds3", "001fe2a1b2c3/direct"));

            var connect = PadForge.Engine.RemoteLink.LinkConnection.EncodeDeviceList(devices, "");
            Assert.True(connect.Length > PadForge.Engine.RemoteLink.LinkConnection.OldPeerPayloadBudget);
            Assert.Null(PadForge.Engine.RemoteLink.LinkConnection.DecodeDeviceList(connect).Last().Ds3Identity);

            var full = PadForge.Engine.RemoteLink.LinkConnection.EncodeDeviceList(devices, "",
                PadForge.Engine.RemoteLink.LinkConnection.MaxListPayload);
            Assert.True(full.Length <= PadForge.Engine.RemoteLink.LinkConnection.MaxListPayload);
            Assert.Equal("001fe2a1b2c3/direct",
                PadForge.Engine.RemoteLink.LinkConnection.DecodeDeviceList(full).Last().Ds3Identity);
        }

        /// <summary>A tail only a full-list peer takes never costs a device its
        /// names: the names get the room the older budget leaves them.</summary>
        [Fact]
        public void ALargerTailBudgetLeavesTheNamesAsTheyWere()
        {
            var named = Ds3("named", null);
            named.SerialNumber = new string('s', 300);
            named.DeviceObjects = Enumerable.Range(0, 40).Select(i => new DeviceObjectItem
            {
                Name = "Key " + i, InputIndex = i, ObjectType = DeviceObjectTypeFlags.PushButton,
            }).ToArray();
            var keyboard = Ds3("kb", null);
            keyboard.AnalogKeyOrder = Enumerable.Range(1, 1790).ToArray();
            var devices = new[] { named, keyboard };

            var connect = PadForge.Engine.RemoteLink.LinkConnection.DecodeDeviceList(
                PadForge.Engine.RemoteLink.LinkConnection.EncodeDeviceList(devices, ""));
            var full = PadForge.Engine.RemoteLink.LinkConnection.DecodeDeviceList(
                PadForge.Engine.RemoteLink.LinkConnection.EncodeDeviceList(devices, "",
                    PadForge.Engine.RemoteLink.LinkConnection.MaxListPayload));
            Assert.Null(connect[1].AnalogKeyOrder);
            Assert.Equal(1790, full[1].AnalogKeyOrder.Length);
            Assert.NotEmpty(full[0].DeviceObjects);
            Assert.Equal(connect[0].DeviceObjects.Select(o => o.Name), full[0].DeviceObjects.Select(o => o.Name));
        }

        /// <summary>The receiving PC names a shared pad by the identity its
        /// owner sent, without reading a node, so a press of Calibrate Gyro
        /// keys on the pad and may take its large resting offset. With no
        /// identity it can't be known, as before.</summary>
        [Fact]
        public void ASharedPadIsNamedByItsOwner()
        {
            Ds3UnitIdentity.ResetForTests();
            int reads = 0;
            Ds3UnitIdentity.NodeReader = _ => { reads++; return "local/node:1"; };
            var info = Ds3("ds3", "001fe2a1b2c3/node:3.20.1.0/cal");
            using var proxy = new PadForge.Engine.RemoteLink.RemotePeerDevice(info);
            try
            {
                var ud = new UserDevice
                {
                    InstanceGuid = Guid.NewGuid(), VendorId = 0x054C, ProdId = 0x0268,
                    Device = proxy, DevicePath = proxy.DevicePath,
                };
                Ds3UnitIdentity.Prime(ud);
                Assert.Equal("001fe2a1b2c3/node:3.20.1.0/cal", Ds3UnitIdentity.Identity(ud));
                info.Ds3Identity = null;
                Assert.Null(Ds3UnitIdentity.Identity(ud));
                Assert.Equal(0, reads);
            }
            finally { Ds3UnitIdentity.ResetForTests(); }
        }
    }

    /// <summary>Delta-audit 2026-10-02: a device list too large for an older
    /// peer leaves a line in the log when it starts and when it ends.</summary>
    public class AuditDelta20261002ListSizeTests
    {
        /// <summary>Every two-second push to a peer older than 3.6.0 failed
        /// the same way once the list outgrew its 4 KB buffer, and only the
        /// last error recorded it. The log now gets one line when the list
        /// stops fitting and one when it fits again, never one per push.</summary>
        [Fact]
        public void TheLogNotesWhenTheListStopsAndStartsFitting()
        {
            int state = 0;
            string Note(PadForge.Engine.RemoteLink.LinkConnectionLifetime.InventoryResult r)
                => PadForge.Engine.RemoteLink.LinkServer.ListSizeNote(ref state, r, "abcd1234");

            Assert.Null(Note(PadForge.Engine.RemoteLink.LinkConnectionLifetime.InventoryResult.Sent));
            Assert.StartsWith("DEVLIST too large for peer abcd1234", Note(PadForge.Engine.RemoteLink.LinkConnectionLifetime.InventoryResult.TooLarge));
            Assert.Null(Note(PadForge.Engine.RemoteLink.LinkConnectionLifetime.InventoryResult.TooLarge));
            Assert.Null(Note(PadForge.Engine.RemoteLink.LinkConnectionLifetime.InventoryResult.Stale));
            Assert.Null(Note(PadForge.Engine.RemoteLink.LinkConnectionLifetime.InventoryResult.Unavailable));
            Assert.Equal(1, state);
            Assert.Equal("DEVLIST fits again for peer abcd1234", Note(PadForge.Engine.RemoteLink.LinkConnectionLifetime.InventoryResult.Sent));
            Assert.Null(Note(PadForge.Engine.RemoteLink.LinkConnectionLifetime.InventoryResult.Sent));
            Assert.Equal(0, state);
        }
    }

    /// <summary>Delta-audit 2026-10-02, second review: the runtime wiring no
    /// unit test reaches, pinned by its source.</summary>
    public class AuditDelta20261002WiringTests
    {
        private static string Text(params string[] parts) => AuditDelta20261002Tests.RepoText(parts);

        private static string MethodBody(string src, string signature)
        {
            int at = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, signature + " moved");
            return src[at..src.IndexOf("\n        }", at, StringComparison.Ordinal)];
        }

        /// <summary>Every assignment that creates its slot stamps the slot's
        /// wire before marking it created, so the first live change on the
        /// slot is never read as a restore (X2).</summary>
        [Fact]
        public void EveryAutoCreatedSlotStampsItsWireFirst()
        {
            string src = Text("PadForge.App", "Services", "DeviceService.cs");
            var creates = Regex.Matches(src, @"SettingsManager\.SlotCreated\[(\w+)\] = true;");
            Assert.True(creates.Count >= 3, creates.Count + " create sites");
            foreach (Match m in creates)
            {
                int from = Math.Max(0, m.Index - 300);
                Assert.Contains("SettingsManager.StampNintendoWire(" + m.Groups[1].Value + ",",
                    src.Substring(from, m.Index - from));
            }
        }

        /// <summary>The DualShock 3 (SIXAXIS): Full preset's motor fields reach
        /// the slot's rumble and the inbound pack from the decoded-output
        /// handler (R1).</summary>
        [Fact]
        public void TheDecodedOutputHandlerFeedsTheDs3Motors()
        {
            string src = Text("PadForge.App", "Common", "Input", "HMaestroVirtualController.cs");
            int handler = src.IndexOf("_controller.OutputDecoded += (ctrl, e) =>", StringComparison.Ordinal);
            int next = src.IndexOf("_controller.OutputReceived += (ctrl, pkt) =>", Math.Max(handler, 0), StringComparison.Ordinal);
            Assert.True(handler >= 0 && next > handler, "the decoded-output handler moved");
            string body = src[handler..next];
            int branch = body.IndexOf("else if (TryDualShock3Motors(e.Fields, e.RawBytes.Length, declaredSize,", StringComparison.Ordinal);
            Assert.True(branch >= 0, "the DualShock 3 motor branch is gone");
            string tail = body[branch..];
            Assert.Contains("vibrationStates[idx].LeftMotorSpeed = ds3Large;", tail);
            Assert.Contains("vibrationStates[idx].RightMotorSpeed = ds3Small;", tail);
            Assert.Contains("LfeOutputState.Pack(ds3Large, ds3Small, 0, 0)", tail);
        }

        /// <summary>The owner sends each DualShock 3's identity, and the
        /// receiving PC keeps a changed one on the row it already has
        /// (A3).</summary>
        [Fact]
        public void TheIdentityLeavesTheOwnerAndReachesAnExistingRow()
        {
            string expose = MethodBody(Text("PadForge.App", "Services", "InputService.cs"),
                "private IReadOnlyList<RemotePeerDeviceInfo> BuildExposedDevices()");
            Assert.Contains("Ds3Identity = PadForge.Engine.DualShock3Motion.Is(ud.VendorId, ud.ProdId)", expose);
            Assert.Contains("PadForge.Common.Input.Ds3UnitIdentity.Identity(ud)", expose);

            string reconcile = MethodBody(Text("PadForge.Engine", "RemoteLink", "LinkServer.cs"),
                "private void ReconcileRemoteDevices(");
            Assert.Contains("existing.Info.Ds3Identity = info.Ds3Identity;", reconcile);
        }

        /// <summary>A publication to a peer that reads full lists gives the
        /// metadata tails that peer's budget, and the connect-time list, the
        /// one list an older peer can receive, keeps the older budget
        /// (A3).</summary>
        [Fact]
        public void APublicationToAFullListPeerGivesTheTailsItsBudget()
        {
            string lifetime = Text("PadForge.Engine", "RemoteLink", "LinkConnectionLifetime.cs");
            Assert.Contains("LinkConnection.EncodeDeviceList(wire, tailBudget: LinkConnection.MaxListPayload)", lifetime);
            string connection = Text("PadForge.Engine", "RemoteLink", "LinkConnection.cs");
            Assert.Contains("byte[] listPayload = EncodeDeviceList(exposeLocal ?? Array.Empty<RemotePeerDeviceInfo>());", connection);
        }

        /// <summary>The MIDI settings load writes the saved descriptors back
        /// under the reload guard, set after the rebuild, whose own refresh
        /// clears it, so the grid's edit hooks never read the load as the
        /// user's binding.</summary>
        [Fact]
        public void TheMidiLoadWritesDescriptorsUnderTheReloadGuard()
        {
            string body = MethodBody(Text("PadForge.App", "Services", "SettingsService.cs"),
                "private void ApplyMidiConfigs(");
            int guard = body.IndexOf("InputService.SuppressMappingEditPush = true;", StringComparison.Ordinal);
            Assert.True(guard >= 0, "no reload guard");
            Assert.True(body.IndexOf("RebuildMappings();", StringComparison.Ordinal) < guard);
            Assert.True(guard < body.IndexOf("mapping.LoadDescriptor(value);", StringComparison.Ordinal));
            Assert.True(guard < body.IndexOf("mapping.LoadNegDescriptor(negValue);", StringComparison.Ordinal));
            Assert.Contains("InputService.SuppressMappingEditPush = suppressed;", body);
        }
    }

    /// <summary>Delta-audit 2026-10-02 contracts (e667d5f7..c774d8fd).</summary>
    public class AuditDelta20261002Tests
    {
        internal static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        // ── X1: a source field that saves marks the file dirty ──────────

        /// <summary>Every field MappingSourceItem.ToDomain saves is on the
        /// list that marks the file dirty when it changes. Rapid Trigger's
        /// distance shipped without its entry, so a Distance edit made on its
        /// own never reached the engine and was lost on close.</summary>
        [Fact]
        public void EverySourceFieldThatSavesMarksTheFileDirty()
        {
            string item = RepoText("PadForge.App", "ViewModels", "MappingSourceItem.cs");
            int start = item.IndexOf("public Engine.Data.MappingSource ToDomain() => new()", StringComparison.Ordinal);
            Assert.True(start >= 0, "ToDomain moved");
            int end = item.IndexOf("};", start, StringComparison.Ordinal);
            var saved = Regex.Matches(item.Substring(start, end - start), @"^\s+(\w+) = ", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value).ToList();
            Assert.Contains("ParamRapidTriggerDistance", saved);

            string window = RepoText("PadForge.App", "MainWindow.xaml.cs");
            int wire = window.IndexOf("msi.PropertyChanged += (s, e) =>", StringComparison.Ordinal);
            Assert.True(wire >= 0, "the extra-source dirty wiring moved");
            string filter = window.Substring(wire, window.IndexOf("_settingsService.MarkDirty();", wire, StringComparison.Ordinal) - wire);
            foreach (string field in saved)
                Assert.True(filter.Contains($"nameof(MappingSourceItem.{field})", StringComparison.Ordinal),
                    $"{field} saves but does not mark the file dirty");
        }

        /// <summary>The app hands the engine its placement lookup, so the
        /// Gamepad names read a Bliss-Box port read raw through the placement
        /// of the controller in it (B2), and a PC with no port open raw skips
        /// the device lookup.</summary>
        [Fact]
        public void TheAppHandsTheEngineThePortPlacements()
        {
            Assert.Contains("SourceCoercion.GamepadPlacementProvider = InputManager.GamepadPlacementFor;",
                AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "InputService.cs"));
            Assert.Contains("BlissBoxRuntime.Ports.Length == 0 ? null : BlissBoxRuntime.GamepadMapFor(LookupUserDevice(deviceGuid))",
                AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "InputManager.Step3.MappingSetEval.cs"));
        }
    }
}
