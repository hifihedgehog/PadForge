using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.BlissBox;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using PadForge.Engine.RemoteLink;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// "(Any Device)" reads only the axes a device has, #431's rule at the
    /// grain of one input. A touchpad, a keyboard, a motion sensor and a
    /// MIDI port answer the wildcard, but a device holds 0 where it has no
    /// axis, and 0 reads as full deflection on a stick read. A web touchpad
    /// beside a centered controller pinned an any-device stick row on its
    /// own pass, won Step 4's merge, and engaged a device-free stick menu.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AnyDeviceMissingAxisTests : IDisposable
    {
        private const int Slot = 5;
        private const int MenuId = 9;

        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly bool[] _enabled = (bool[])SettingsManager.SlotEnabled.Clone();
        private readonly MappingSet _set = new();

        public AnyDeviceMissingAxisTests()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            Array.Clear(SettingsManager.SlotCreated);
            Array.Clear(SettingsManager.SlotEnabled);
            SettingsManager.SlotCreated[Slot] = true;
            SettingsManager.SlotEnabled[Slot] = true;
            SettingsManager.SlotMappingSets[Slot] = _set;
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            InputManager.ClearSourceKindRuntime();
        }

        public void Dispose()
        {
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            InputManager.ClearSourceKindRuntime();
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.SlotMappingSets = _sets;
            Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
            Array.Copy(_enabled, SettingsManager.SlotEnabled, _enabled.Length);
        }

        // ── Devices ─────────────────────────────────────────────────────

        private static void Assign(UserDevice ud)
        {
            SettingsManager.UserDevices.Items.Add(ud);
            var setting = new UserSetting { InstanceGuid = ud.InstanceGuid, ProductGuid = ud.InstanceGuid, MapTo = Slot };
            setting.SetPadSetting(new PadSetting());
            SettingsManager.UserSettings.Items.Add(setting);
        }

        /// <summary>A row loaded from a live web controller, so its recorded
        /// capabilities are the wrapper's.</summary>
        private static UserDevice Loaded(WebControllerDevice web)
        {
            web.SetConnected(true);
            var ud = new UserDevice();
            ud.LoadFromWebDevice(web);
            ud.Device = web;
            ud.IsOnline = true;
            Assign(ud);
            return ud;
        }

        /// <summary>A gamepad client at rest: six axes, sticks centered,
        /// triggers released.</summary>
        private static UserDevice Pad()
        {
            var ud = Loaded(new WebControllerDevice("pad-" + Guid.NewGuid().ToString("N"), "Pad"));
            var state = new CustomInputState();
            Array.Fill(state.Axis, 32768);
            state.Axis[2] = state.Axis[5] = 0;
            ud.InputState = state;
            return ud;
        }

        /// <summary>The reported device: a web touchpad client, which has no
        /// numbered axes and leaves Axis[] at 0.</summary>
        private static UserDevice Touchpad()
        {
            var web = new WebControllerDevice("tp-" + Guid.NewGuid().ToString("N"), "Touchpad", isTouchpad: true);
            var ud = Loaded(web);
            ud.InputState = web.GetCurrentState();
            return ud;
        }

        /// <summary>A keyboard row as its wrapper records it: no axes
        /// (SdlKeyboardWrapper.NumAxes is 0), Axis[] left at 0.</summary>
        private static UserDevice Keyboard()
        {
            var ud = Touchpad();
            ud.CapType = InputDeviceType.Keyboard;
            ud.CapAxeCount = 0;
            return ud;
        }

        /// <summary>A mouse row as its wrapper records it: motion and wheel
        /// on axes 0 to 2 (SdlMouseWrapper.MouseAxes), centered at rest, and
        /// nothing past them.</summary>
        private static UserDevice Mouse()
        {
            var ud = Touchpad();
            ud.CapType = InputDeviceType.Mouse;
            ud.CapAxeCount = 3;
            ud.InputState = new CustomInputState();
            for (int i = 0; i < 3; i++) ud.InputState.Axis[i] = 32767;
            return ud;
        }

        /// <summary>A device of whatever axis shape a test needs, reporting it
        /// through the members SdlDeviceWrapper or a peer copy would.</summary>
        private sealed class ShapedDevice : WebControllerDevice, ISdlInputDevice
        {
            public ShapedDevice() : base("shaped-" + Guid.NewGuid().ToString("N"), "Shaped") { }
            public new IntPtr GamepadHandle { get; set; }
            public new int NumAxes { get; set; }
            public int RawAxisCount { get; set; }
            public bool HasExtraGenericAxes { get; set; }
            public new int[] SupportedAxisIndices { get; set; }
            public DeviceObjectItem[] Objects { get; set; } = Array.Empty<DeviceObjectItem>();
            public new DeviceObjectItem[] GetDeviceObjects() => Objects;
            public int DeviceType { get; set; } = InputDeviceType.Joystick;
            public new int GetInputDeviceType() => DeviceType;
            public new ushort VendorId { get; set; }
            public new ushort ProductId { get; set; }
        }

        /// <summary>A row loaded from <paramref name="device"/> the way
        /// Step 1 loads a wrapper, its state all zero.</summary>
        private static UserDevice Row(ISdlInputDevice device)
        {
            var ud = new UserDevice();
            ud.LoadFromExternalDevice(device);
            ud.IsOnline = true;
            ud.InputState = new CustomInputState();
            Assign(ud);
            return ud;
        }

        private static DeviceObjectItem AxisObject(int index) => new()
        {
            InputIndex = index, ObjectTypeGuid = ObjectGuid.ZAxis, Name = "Axis " + index,
            ObjectType = DeviceObjectTypeFlags.AbsoluteAxis, Offset = index * 4,
        };

        private static DeviceObjectItem MotionObject(int index) => new()
        {
            InputIndex = index, ObjectTypeGuid = ObjectGuid.XAxis, Name = "Mouse " + index,
            ObjectType = DeviceObjectTypeFlags.RelativeAxis, Offset = index * 4,
        };

        private static DeviceObjectItem[] AxisObjects(params int[] indices) => indices.Select(AxisObject).ToArray();

        /// <summary>A peer copy with its own id. The info's ids default to
        /// empty strings, and two copies sharing one minted the same
        /// InstanceGuid, so a pass read the first row for both.</summary>
        private static RemotePeerDevice Peer(RemotePeerDeviceInfo info)
        {
            if (string.IsNullOrEmpty(info.PeerFingerprintHex)) info.PeerFingerprintHex = "0123456789abcdef0123456789abcdef";
            if (string.IsNullOrEmpty(info.PeerLocalDeviceId)) info.PeerLocalDeviceId = Guid.NewGuid().ToString("N");
            if (string.IsNullOrEmpty(info.Name)) info.Name = "Peer";
            info.Online = true;
            return new RemotePeerDevice(info);
        }

        private void AddLayerRow(string target, string layer, params MappingSource[] sources)
            => _set.Rows.Add(new MappingRow { Target = target, LayerMask = layer, Sources = sources.ToList() });

        private static MappingSource Any(string descriptor) => new() { DeviceGuid = "", Descriptor = descriptor };

        private static MappingSource Named(UserDevice ud, string descriptor)
            => new() { DeviceGuid = ud.InstanceGuidString, Descriptor = descriptor };

        /// <summary>A half-axis source that reads pressed below center: 0
        /// reads fully pushed.</summary>
        private static MappingSource AnyNegativeHalf(string descriptor)
            => new() { DeviceGuid = "", Descriptor = descriptor, HalfAxis = true, Invert = true };

        private void AddRow(string target, params MappingSource[] sources)
            => _set.Rows.Add(new MappingRow { Target = target, LayerMask = "Base", Sources = sources.ToList() });

        private static readonly MethodInfo BeginFrame = typeof(InputManager)
            .GetMethod("BeginFrameMultiSourceTracking", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo Apply = typeof(InputManager)
            .GetMethod("ApplyMappingSetToGamepad", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>One Step 3 pass for one device, the way UpdateOutputStates
        /// runs it (AnyDeviceSourceEligibilityTests.Pass).</summary>
        private Gamepad Pass(UserDevice ud)
        {
            BeginFrame.Invoke(null, null);
            object[] args = { ud.InputState, _set, ud.InstanceGuidString, 50, Slot, new Gamepad() };
            Apply.Invoke(null, args);
            return (Gamepad)args[5];
        }

        // ── The descriptor and capability tests ─────────────────────────

        [Theory]
        [InlineData("Axis 3", 1, 3, -1)]
        [InlineData("Gamepad LeftStickX", 1, 0, -1)]
        [InlineData("Gamepad RightTrigger", 1, 5, -1)]
        [InlineData("Slider 2", 1, 26, -1)]
        [InlineData("Gamepad LeftStickRing", 2, 0, 1)]
        [InlineData("Gamepad RightStickRing", 2, 3, 4)]
        [InlineData("Flick Stick Right", 2, 3, 4)]
        [InlineData("Flick Stick Touchpad 0", 0, -1, -1)]
        [InlineData("Button 3", 0, -1, -1)]
        [InlineData("Gamepad ButtonA", 0, -1, -1)]
        [InlineData("Touchpad 0 Finger 0 X", 0, -1, -1)]
        [InlineData("Gyro Pitch", 0, -1, -1)]
        [InlineData("", 0, -1, -1)]
        public void TheAxesADescriptorReads(string descriptor, int count, int first, int second)
        {
            Assert.Equal(count, SourceCoercion.NumberedAxesRead(descriptor, out int a, out int b));
            Assert.Equal(first, a);
            Assert.Equal(second, b);
        }

        [Theory]
        [InlineData("Axis 3", 3)]
        [InlineData("Gamepad LeftStickY", 1)]
        [InlineData("Axis 23", 23)]
        [InlineData("Axis 24", -1)]
        [InlineData("Slider 2", -1)]
        [InlineData("Gamepad RightStickRing", -1)]
        [InlineData("Flick Stick Right", -1)]
        [InlineData("Button 0", -1)]
        [InlineData("", -1)]
        public void TheAxisASteeringReadTakes(string descriptor, int axis)
            => Assert.Equal(axis, SourceKindRuntime.SteeringAxisRead(descriptor));

        [Fact]
        public void ADeviceHasTheAxesItsWrapperRecorded()
        {
            // No wrapper loaded the counts: every axis, the auto-map's
            // capability-less fallback.
            var ghost = new UserDevice();
            Assert.True(ghost.HasAxis(0));
            Assert.True(ghost.HasAxis(30));

            var web = new WebControllerDevice("cap-" + Guid.NewGuid().ToString("N"), "Pad");
            var dense = new UserDevice { Device = web, CapAxeCount = 3 };
            Assert.True(dense.HasAxis(2));
            Assert.False(dense.HasAxis(3));
            Assert.False(dense.HasAxis(-1));

            var sparse = new UserDevice { Device = web, CapAxeCount = 2, CapAxisIndices = new[] { 3, 4 } };
            Assert.True(sparse.HasAxis(4));
            Assert.False(sparse.HasAxis(0));

            // Slider N is the overflow axis 24 + N.
            var flight = new UserDevice { Device = web, CapAxeCount = 24, RawAxisCount = 26 };
            Assert.True(flight.HasAxis(CustomInputState.MaxAxis + 1));
            Assert.False(flight.HasAxis(CustomInputState.MaxAxis + 2));

            // The web touchpad records none.
            var tp = Touchpad();
            Assert.Equal(0, tp.CapAxeCount);
            Assert.False(tp.HasAxis(0));
        }

        // ── One-source rows, the gamepad lane ───────────────────────────

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ATouchpadOrKeyboardPass_ReadsStickRowsAtRest_AndStillAnswersButtons(bool keyboard)
        {
            var pad = Pad();
            var device = keyboard ? Keyboard() : Touchpad();
            AddRow("LeftThumbAxisX", Any("Gamepad LeftStickX"));
            AddRow("RightThumbAxisY", Any("Axis 4"));
            AddRow("LeftTrigger", AnyNegativeHalf("Axis 0"));
            AddRow("ButtonA", Any("Gamepad ButtonA"));
            device.InputState.Buttons[0] = true;

            var gp = Pass(device);
            Assert.Equal((short)0, gp.ThumbLX);
            Assert.Equal((short)0, gp.ThumbRY);
            Assert.Equal((ushort)0, gp.LeftTrigger);
            // The gate is per input: the device's buttons still answer.
            Assert.True(gp.IsButtonPressed(Gamepad.A));

            // The pad still reads through the same rows.
            pad.InputState.Axis[0] = 65535;
            Assert.Equal((short)32767, Pass(pad).ThumbLX);
        }

        [Fact]
        public void AMousePass_ReadsItsOwnAxes_AndRestsTheOnesItLacks()
        {
            var mouse = Mouse();
            AddRow("LeftThumbAxisX", Any("Axis 0"));
            AddRow("RightThumbAxisX", Any("Axis 3"));
            mouse.InputState.Axis[0] = 65535;

            var gp = Pass(mouse);
            Assert.Equal((short)32767, gp.ThumbLX);
            Assert.Equal((short)0, gp.ThumbRX);
        }

        [Fact]
        public void PublicEvaluators_ReadRestFromADeviceWithoutTheAxis()
        {
            var kb = Keyboard();
            AddRow("ButtonA", AnyNegativeHalf("Axis 0"));
            AddRow("LeftThumbAxisX", Any("Gamepad LeftStickX"));
            AddRow("ExtTrigger1", AnyNegativeHalf("Axis 0"));
            AddRow("TouchpadX1", Any("Axis 0"));
            string g = kb.InstanceGuidString;

            Assert.True(InputManager.TryEvaluateMappingSetButton(kb.InputState, _set, g, Slot, "ButtonA", 50, out bool a));
            Assert.False(a);
            Assert.True(InputManager.TryEvaluateMappingSetBipolarAxis(kb.InputState, _set, g, Slot, "LeftThumbAxisX", out short x));
            Assert.Equal((short)0, x);
            Assert.True(InputManager.TryEvaluateMappingSetRawTrigger(kb.InputState, _set, g, Slot, "ExtTrigger1", out short t));
            Assert.Equal(short.MinValue, t);
            // No active source: the touchpad output holds its position.
            Assert.False(InputManager.TryEvaluateMappingSetTouchpadAxis(kb.InputState, _set, g, Slot, "TouchpadX1", 0, out _));
        }

        // ── Rows read once a frame across the slot ──────────────────────

        [Fact]
        public void MultiSourceRows_SpanOnlyDevicesWithTheAxis()
        {
            var pad = Pad();
            Touchpad();
            _set.Rows.Add(new MappingRow
            {
                Target = "LeftThumbAxisX", LayerMask = "Base",
                Sources = new[] { Any("Gamepad LeftStickX"), Any("Gamepad RightStickX") }.ToList(),
            });
            _set.Rows.Add(new MappingRow
            {
                Target = "LeftTrigger", LayerMask = "Base",
                Sources = new[] { AnyNegativeHalf("Axis 0"), Any("Gamepad RightTrigger") }.ToList(),
            });
            _set.Rows.Add(new MappingRow
            {
                Target = "ButtonA", LayerMask = "Base",
                Sources = new[] { AnyNegativeHalf("Axis 0"), Any("Gamepad ButtonB") }.ToList(),
            });

            // The pad's pass reads each row once for the whole slot, and the
            // touchpad on the slot has none of these axes.
            var gp = Pass(pad);
            Assert.Equal((short)0, gp.ThumbLX);
            Assert.Equal((ushort)0, gp.LeftTrigger);
            Assert.False(gp.IsButtonPressed(Gamepad.A));

            pad.InputState.Axis[3] = 65535;
            Assert.Equal((short)32767, Pass(pad).ThumbLX);
        }

        [Fact]
        public void EachSideOfANegativePair_ReadsOnlyADeviceWithItsAxis()
        {
            // Opposite Invert makes the second source the first one's
            // negative side, read per device with it. The mouse has Axis 0 at
            // center and no Axis 4, so a side that read its zeroed Axis 4
            // would show alone: nothing on the other side cancels it.
            var mouse = Mouse();
            MappingSource Inverted(string descriptor)
            {
                var s = Any(descriptor);
                s.Invert = true;
                return s;
            }
            _set.Rows.Add(new MappingRow
            {
                Target = "RightThumbAxisX", LayerMask = "Base",
                Sources = new List<MappingSource> { Any("Axis 0"), Inverted("Axis 4") },
            });
            _set.Rows.Add(new MappingRow
            {
                Target = "LeftThumbAxisX", LayerMask = "Base",
                Sources = new List<MappingSource> { Any("Axis 4"), Inverted("Axis 0") },
            });
            // The mouse rests a count below the midpoint (SdlMouseWrapper's
            // AxisCenter), and the zeroed axis would read a full deflection.
            var gp = Pass(mouse);
            Assert.InRange(gp.ThumbRX, (short)-1, (short)1);
            Assert.InRange(gp.ThumbLX, (short)-1, (short)1);
        }

        [Fact]
        public void ASteeringSource_ReadsRestFromADeviceWithoutItsYAxis()
        {
            // The mouse has the X axis (0) but not the Y axis (4) the
            // steering source reads from ParamYDescriptor.
            var mouse = Mouse();
            _set.Rows.Add(new MappingRow
            {
                Target = "LeftThumbAxisY", LayerMask = "Base",
                Sources = new List<MappingSource>
                {
                    new() { DeviceGuid = "", Descriptor = "Axis 0", ParamYDescriptor = "Axis 4", Kind = "AngleToAxisY" },
                },
            });
            // The Y axis alone is numbered, and the source still looks its
            // device up.
            _set.Rows.Add(new MappingRow
            {
                Target = "RightThumbAxisY", LayerMask = "Base",
                Sources = new List<MappingSource>
                {
                    new() { DeviceGuid = "", Descriptor = "Button 0", ParamYDescriptor = "Axis 4", Kind = "AngleToAxisY" },
                },
            });
            var gp = Pass(mouse);
            Assert.Equal((short)0, gp.ThumbLY);
            Assert.Equal((short)0, gp.ThumbRY);
        }

        [Fact]
        public void AStickTrim_IsNotHeldOpenByADeviceWithoutTheGateAxis()
        {
            var pad = Pad();
            Touchpad();
            _set.Rows.Add(new MappingRow
            {
                Target = "LeftTrigger", LayerMask = "Base", CombineMode = "StickTrim",
                Sources = new List<MappingSource> { AnyNegativeHalf("Axis 0"), Any("Gamepad RightStickX") },
            });
            Assert.Equal((ushort)0, Pass(pad).LeftTrigger);
        }

        [Fact]
        public void AStickTrim_TakesNoTrimFromADeviceWithoutTheAxis()
        {
            var pad = Pad();
            Touchpad();
            // The trigger holds the trim open. The trim axis is inverted, so
            // a zeroed axis would read +1 and walk the level down.
            var axis = Any("Gamepad LeftStickX");
            axis.Invert = true;
            _set.Rows.Add(new MappingRow
            {
                Target = "LeftTrigger", LayerMask = "Base", CombineMode = "StickTrim",
                Sources = new List<MappingSource> { Any("Gamepad LeftTrigger"), axis },
            });
            pad.InputState.Axis[2] = 65535;

            ushort level = Pass(pad).LeftTrigger;
            Assert.Equal((ushort)65535, level);
            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(20);
                level = Pass(pad).LeftTrigger;
            }
            Assert.Equal((ushort)65535, level);

            // The pad's own stick walks it.
            pad.InputState.Axis[0] = 0;
            for (int i = 0; i < 10 && level == 65535; i++)
            {
                Thread.Sleep(20);
                level = Pass(pad).LeftTrigger;
            }
            Assert.True(level < 65535);
        }

        // ── Activators, menus, macro triggers, the flick lane ───────────

        [Fact]
        public void AnAnyDeviceAxisActivator_IgnoresADeviceWithoutTheAxis()
        {
            var pad = Pad();
            var tp = Touchpad();
            _set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "", Kind = "Axis", Descriptor = "Gamepad LeftStickX",
                AxisThreshold = 0.5, Mode = "Toggle", LayerMask = "Lean",
            });
            _set.Rows.Add(new MappingRow
            {
                Target = "ButtonB", LayerMask = "Lean",
                Sources = new List<MappingSource> { Named(pad, "Button 1") },
            });
            pad.InputState.Buttons[1] = true;

            // Neither the touchpad's own pass nor the pad's reading of the
            // slot engages the layer through the touchpad's missing axis. A
            // Toggle latches on the press edge, so one pass that read the
            // missing axis as a push would hold the layer on.
            Pass(tp);
            Assert.False(Pass(pad).IsButtonPressed(Gamepad.B));
            Pass(tp);
            Assert.False(Pass(pad).IsButtonPressed(Gamepad.B));

            // The pad's stick toggles it on.
            pad.InputState.Axis[0] = 65535;
            Pass(pad);
            Assert.True(Pass(pad).IsButtonPressed(Gamepad.B));
        }

        [Fact]
        public void ADeviceFreeStickMenu_StaysClosedOnADeviceWithoutTheAxes()
        {
            var engineField = typeof(InputService).GetField("_inputManagerStatic", BindingFlags.Static | BindingFlags.NonPublic);
            object engine = engineField.GetValue(null);
            var im = new InputManager();
            try
            {
                engineField.SetValue(null, im);
                var menu = new MenuDefinitionEntry
                {
                    MenuId = MenuId, Kind = MenuKind.Radial, CellCount = 4,
                    HostDescriptor = "Gamepad RightStick", EngageDeadzonePercent = 25,
                };
                for (int i = 0; i < 4; i++) menu.Items.Add(new MenuItemDefinition { Index = i, VirtualKey = 0x41 + i });
                _set.Menus.Add(menu);
                var tp = Touchpad();
                var pad = Pad();

                using (InputManager.EnterMenuPublication())
                    im.UpdateMenuContexts(tp, tp.InputState);
                Assert.False(im.MenuContexts[(Slot, tp.InstanceGuid, MenuId)].State.Engaged);

                pad.InputState.Axis[3] = 65535;
                using (InputManager.EnterMenuPublication())
                    im.UpdateMenuContexts(pad, pad.InputState);
                Assert.True(im.MenuContexts[(Slot, pad.InstanceGuid, MenuId)].State.Engaged);
            }
            finally
            {
                engineField.SetValue(null, engine);
            }
        }

        [Fact]
        public void AMacroAxisTrigger_DeviceFreeSkipsADeviceWithoutTheAxis_ANamedOneReadsItsDevice()
        {
            // Pushed left, the inverted entry's direction. The device's
            // recorded axes are the right stick's alone.
            var entry = new MacroItem.TriggerInputEntry { AxisTarget = MacroAxisTarget.LeftStickX, Invert = true, DeadZone = 50 };
            var anySlot = typeof(InputManager).GetMethod("AnySlotDeviceAxisEntryActive", BindingFlags.NonPublic | BindingFlags.Instance);
            var named = typeof(InputManager).GetMethod("TriggerAxisEntryActive", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(anySlot);
            Assert.NotNull(named);
            var stick = Row(new ShapedDevice { NumAxes = 5, Objects = AxisObjects(3, 4) });

            Assert.False((bool)anySlot.Invoke(new InputManager(), new object[] { Slot, entry }));
            // An entry that names its device reads it as it is: the gate is
            // the wildcard's, and a false positive there would hide real input.
            Assert.True((bool)named.Invoke(null, new object[] { stick, entry }));

            var pad = Pad();
            pad.InputState.Axis[0] = 0;
            Assert.True((bool)anySlot.Invoke(new InputManager(), new object[] { Slot, entry }));
        }

        [Fact]
        public void TheKbmFlickLane_ReadsNoStickFromADeviceWithoutIt()
        {
            _set.Rows.Add(new MappingRow
            {
                Target = "KbmMouseX", LayerMask = "Base",
                Sources = new List<MappingSource> { Any(SourceCoercion.FlickStickLeftDescriptor) },
            });
            var tick = typeof(InputManager).GetMethod("TickFlickStickSources", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(tick);
            var flickState = typeof(SourceKindRuntime).GetField("_flickState", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(flickState);
            int FlickStates() => ((ICollection)flickState.GetValue(InputManager.GetSlotSourceKindRuntime(Slot))).Count;

            // A read ticks the flick state, so none exists after the
            // touchpad's pass.
            var tp = Touchpad();
            tick.Invoke(null, new object[] { tp.InputState, _set, tp.InstanceGuidString, Slot });
            Assert.Equal(0, FlickStates());

            var pad = Pad();
            tick.Invoke(null, new object[] { pad.InputState, _set, pad.InstanceGuidString, Slot });
            Assert.Equal(1, FlickStates());
        }

        // ── Rows that record their own shape ────────────────────────────

        [Fact]
        public void APrecisionTouchpadRow_ReadsNoStick()
        {
            // Step 1 records a native Precision Touchpad with a count of 0 and
            // no wrapper, and Step 2 reads it into a reset state, every axis 0.
            var pad = Pad();
            var ptp = new UserDevice();
            var g = Guid.NewGuid();
            ptp.LoadInstance(g, "Precision Touchpad", g, "Precision Touchpad");
            ptp.LoadCapabilities(0, 0, 0, InputDeviceType.Touchpad);
            ptp.IsOnline = true;
            ptp.HasTouchpad = true;
            ptp.InputState = new CustomInputState();
            Assign(ptp);
            AddRow("LeftThumbAxisX", Any("Gamepad LeftStickX"));

            Assert.False(ptp.HasAxis(0));
            Assert.Equal((short)0, Pass(ptp).ThumbLX);
            pad.InputState.Axis[0] = 65535;
            Assert.Equal((short)32767, Pass(pad).ThumbLX);
        }

        [Fact]
        public void RawJoystickMode_ReadsTheJoysticksOwnAxes()
        {
            // SDL's gamepad layout records one axis for this pad, its trigger
            // at position 5. Force Raw Joystick Mode reads the joystick's own
            // six (GetJoystickState), and the trigger arrives on Axis 0.
            var pad = Row(new ShapedDevice
            {
                DeviceType = InputDeviceType.Gamepad, GamepadHandle = (IntPtr)1, NumAxes = 6, RawAxisCount = 1,
                SupportedAxisIndices = new[] { 5 }, Objects = AxisObjects(5),
            });
            // The trigger pressed to the raw axis's far end reads 0, which
            // only the raw read's own span tells from a missing axis.
            AddRow("LeftThumbAxisX", Any("Axis 0"));
            pad.InputState.Axis[0] = 0;
            Assert.Equal((short)0, Pass(pad).ThumbLX);

            pad.ForceRawJoystickMode = true;
            Assert.True(Pass(pad).ThumbLX < -32000);
            Assert.True(pad.HasAxis(5));
            Assert.False(pad.HasAxis(6));

            // Generic extras widen the raw read to every raw axis.
            var ds3 = Row(new ShapedDevice
            {
                DeviceType = InputDeviceType.Gamepad, GamepadHandle = (IntPtr)1, NumAxes = 6, RawAxisCount = 16,
                HasExtraGenericAxes = true, Objects = AxisObjects(0, 1, 2, 3, 4, 5),
            });
            ds3.ForceRawJoystickMode = true;
            Assert.True(ds3.HasAxis(15));
            Assert.False(ds3.HasAxis(16));
        }

        [Fact]
        public void ABlissBoxPortsPressureAxes_ReadThroughTheWildcard()
        {
            // A port read raw: eight native axes, and the pressure of the
            // controller in it merged on 8 to 19 (BlissBoxRuntime.MergeInto).
            // Its object list names them only after the UI thread refreshes it,
            // and a peer's copy has no list that names them at all.
            var raw = new SdlDeviceWrapper();
            try
            {
                var port = new UserDevice
                {
                    InstanceGuid = Guid.NewGuid(), VendorId = BlissBoxProtocol.VendorId,
                    ProdId = BlissBoxProtocol.PlayerOneProductId,
                };
                port.LoadCapabilities(8, 24, 1, InputDeviceType.Joystick);
                port.Device = raw;
                port.IsOnline = true;
                port.InputState = new CustomInputState();
                Assign(port);
                var copy = Row(Peer(new RemotePeerDeviceInfo
                {
                    NumAxes = 8, InputDeviceType = InputDeviceType.Joystick,
                    VendorId = BlissBoxProtocol.VendorId, ProductId = (ushort)(BlissBoxProtocol.PlayerOneProductId + 1),
                }));
                // At rest the pressure reads 0, which a DualShock 2's
                // unpressed button also reads, so the range is read whatever
                // the port holds. A stick read of that rest is full deflection
                // on any device that has the axis.
                AddRow("LeftThumbAxisX", Any("Axis 14"));
                foreach (var ud in new[] { port, copy })
                    Assert.True(Pass(ud).ThumbLX < -32000);
                // The twelve end at 19.
                AddRow("RightThumbAxisX", Any("Axis 20"));
                Assert.Equal((short)0, Pass(port).ThumbRX);
            }
            finally
            {
                raw.Dispose();
            }
        }

        [Fact]
        public void EveryAxisADeviceFills_ReadsThroughTheWildcard()
        {
            // The stock web pad, six axes.
            var pad = Pad();
            for (int i = 0; i < 6; i++) Assert.True(pad.HasAxis(i), "pad axis " + i);

            // A mouse's motion and wheel, relative axes 0 to 2
            // (SdlMouseWrapper.GetDeviceObjects).
            var mouse = Row(new ShapedDevice
            {
                DeviceType = InputDeviceType.Mouse, NumAxes = 3,
                Objects = new[] { MotionObject(0), MotionObject(1), MotionObject(2) },
            });
            for (int i = 0; i < 3; i++) Assert.True(mouse.HasAxis(i), "mouse axis " + i);
            Assert.False(mouse.HasAxis(3));

            // A pad's generic extras past the six (#193).
            var extras = Row(new ShapedDevice
            {
                DeviceType = InputDeviceType.Gamepad, GamepadHandle = (IntPtr)1, NumAxes = 6, RawAxisCount = 16,
                HasExtraGenericAxes = true, Objects = AxisObjects(Enumerable.Range(0, 16).ToArray()),
            });
            for (int i = 0; i < 16; i++) Assert.True(extras.HasAxis(i), "extra axis " + i);

            // A peer's thirty-axis stick: 24 in Axis[], six in Sliders[].
            var stick = Row(Peer(new RemotePeerDeviceInfo { NumAxes = 30, InputDeviceType = InputDeviceType.Joystick }));
            for (int i = 0; i < 30; i++) Assert.True(stick.HasAxis(i), "peer axis " + i);
            Assert.False(stick.HasAxis(30));

            // A row no wrapper loaded keeps every axis.
            Assert.True(new UserDevice().HasAxis(29));
        }

        [Fact]
        public void ASourceThatNamesItsDevice_ReadsItAsItIs()
        {
            // The recorded axes are the right stick's, and Axis 0 holds the
            // 0 of a device without it. A named source reads that 0 as it is,
            // and only the wildcard skips it.
            var stick = Row(new ShapedDevice { NumAxes = 5, Objects = AxisObjects(3, 4) });
            AddRow("LeftThumbAxisX", Named(stick, "Axis 0"));
            AddRow("RightThumbAxisX", Any("Axis 0"));

            var gp = Pass(stick);
            Assert.True(gp.ThumbLX < -32000);
            Assert.Equal((short)0, gp.ThumbRX);
        }

        [Fact]
        public void AnAxisADeviceDoesNotList_ReadsWhileItHoldsAValue()
        {
            // Only a 0 in an axis the device does not list is the filler of a
            // device without it. Anything else is live input or a rest the
            // device's read wrote, and the wildcard reads it.
            // The inverted half reads both values as pushed, so only the gate
            // tells them apart.
            var stick = Row(new ShapedDevice { NumAxes = 5, Objects = AxisObjects(3, 4) });
            AddRow("ButtonA", AnyNegativeHalf("Axis 1"));
            stick.InputState.Axis[1] = 1;
            Assert.True(Pass(stick).IsButtonPressed(Gamepad.A));
            stick.InputState.Axis[1] = 0;
            Assert.False(Pass(stick).IsButtonPressed(Gamepad.A));
        }

        [Fact]
        public void ARemoteGamepadCopy_ReadsItsSticksWhileItsListLags()
        {
            // A shared built pad listed one slider, then gained both sticks.
            // The copy lists them at the owner's next device-list push, and
            // each stick at its end reads before then.
            var pad = Row(Peer(new RemotePeerDeviceInfo
            {
                InputDeviceType = InputDeviceType.Gamepad, NumAxes = 6, SupportedAxisIndices = new[] { 5 },
            }));
            Assert.False(pad.HasAxis(0));
            AddRow("LeftThumbAxisX", Any("Axis 0"));
            AddRow("LeftThumbAxisY", Any("Axis 1"));
            AddRow("RightThumbAxisX", Any("Axis 3"));
            AddRow("RightThumbAxisY", Any("Axis 4"));
            AddRow("ButtonA", AnyNegativeHalf("Axis 2"));
            var gp = Pass(pad);
            Assert.True(Math.Abs((int)gp.ThumbLX) > 32000);
            Assert.True(Math.Abs((int)gp.ThumbLY) > 32000);
            Assert.True(Math.Abs((int)gp.ThumbRX) > 32000);
            Assert.True(Math.Abs((int)gp.ThumbRY) > 32000);

            // A trigger position holds 0 when its trigger is missing, so it
            // still reads rest.
            Assert.False(gp.IsButtonPressed(Gamepad.A));
        }

        [Fact]
        public void ARemoteCopyWithoutTheStick_StillReadsRest()
        {
            // A copy of something other than a gamepad, and a gamepad whose
            // owner reads two axes, hold 0 where they have no stick.
            var touchpad = Row(Peer(new RemotePeerDeviceInfo
            {
                InputDeviceType = InputDeviceType.Touchpad, NumAxes = 6, SupportedAxisIndices = Array.Empty<int>(),
            }));
            var twoAxis = Row(Peer(new RemotePeerDeviceInfo
            {
                InputDeviceType = InputDeviceType.Gamepad, NumAxes = 2, SupportedAxisIndices = new[] { 0, 1 },
            }));
            AddRow("LeftThumbAxisX", Any("Axis 0"));
            AddRow("RightThumbAxisX", Any("Axis 3"));
            Assert.Equal((short)0, Pass(touchpad).ThumbLX);
            Assert.Equal((short)0, Pass(twoAxis).ThumbRX);
        }

        [Fact]
        public void ABuiltWebPad_ReadsAnAxisItsPageSendsAfterConnecting()
        {
            // A pad built from one button, then given a stick and saved while
            // its page stayed connected. The stick's full-left 0 is input.
            var web = new WebControllerDevice("built-" + Guid.NewGuid().ToString("N"), "Built Pad",
                layoutKey: "custom:test");
            web.SetCustomSurface(null, new[] { 0 }, hasPov: false);
            var pad = Loaded(web);
            int raised = 0;
            web.CapabilitiesChanged += () => { raised++; pad.LoadFromWebDevice(web); };
            Assert.False(pad.HasAxis(0));
            AddRow("LeftThumbAxisX", Any("Axis 0"));

            web.UpdateAxis(0, 0);
            pad.InputState = web.GetCurrentState();
            Assert.True(Pass(pad).ThumbLX < -32000);
            Assert.True(pad.HasAxis(0));
            Assert.Equal(new[] { 0 }, web.SupportedAxisIndices);

            // The axis joins once, and a stock layout keeps its six.
            web.UpdateAxis(0, 32767);
            Assert.Equal(1, raised);
            var stock = new WebControllerDevice("stock-" + Guid.NewGuid().ToString("N"), "Stock Pad");
            stock.CapabilitiesChanged += () => raised++;
            stock.UpdateAxis(0, 0);
            Assert.Equal(1, raised);
            Assert.Null(stock.SupportedAxisIndices);
        }

        // ── Legs beside the descriptor ──────────────────────────────────

        [Fact]
        public void AGatedSource_IgnoresADeviceWithoutTheGateAxes()
        {
            // A button gated on the right stick's ring, the chord a Workshop
            // import writes. The keyboard's key is down, and its zeroed axes
            // read as the ring pushed to the rim.
            var kb = Keyboard();
            var pad = Pad();
            var gated = Any("Button 0");
            gated.GateDescriptor = SourceCoercion.RightStickRingDescriptor;
            AddRow("ButtonA", gated);
            kb.InputState.Buttons[0] = true;
            Assert.False(Pass(kb).IsButtonPressed(Gamepad.A));

            pad.InputState.Buttons[0] = true;
            pad.InputState.Axis[3] = 65535;
            Assert.True(Pass(pad).IsButtonPressed(Gamepad.A));
        }

        [Fact]
        public void ASteamCircleStickRead_NeedsThePartnerAxis_AndOtherReadsDoNot()
        {
            // A one-axis joystick: Axis 0 and nothing at 1.
            var pedal = Row(new ShapedDevice { NumAxes = 1, Objects = AxisObjects(0) });
            MappingSource Circle() => new()
            {
                DeviceGuid = "", Descriptor = "Axis 0", ParamStickDeadZoneShape = 2, ParamStickDeadZoneInner = 0.2,
            };
            AddRow("LeftThumbAxisX", Circle());
            AddRow("ButtonA", Circle());

            // About 0.1, inside the 0.2 radius. The partner's 0 read as full
            // deflection and pushed the pair past the deadzone.
            pedal.InputState.Axis[0] = 36045;
            Assert.Equal((short)0, Pass(pedal).ThumbLX);

            // The button read takes Axis 0 alone, so it still reads.
            pedal.InputState.Axis[0] = 65535;
            Assert.True(Pass(pedal).IsButtonPressed(Gamepad.A));

            // A pad has both, and its stick reads.
            var pad = Pad();
            pad.InputState.Axis[0] = 49152;
            Assert.NotEqual((short)0, Pass(pad).ThumbLX);
        }

        [Fact]
        public void AWheelWithoutLeftY_SteersThroughACircleDeadzoneAndARing()
        {
            // The Oklick W-2: SDL's database maps leftx and the pedals on the
            // triggers and no lefty, and SDL types it a wheel. The gamepad
            // read writes the Axis 1 it lacks at center, not zeroed filler.
            var wheel = Row(new ShapedDevice
            {
                DeviceType = InputDeviceType.Driving, GamepadHandle = (IntPtr)1, NumAxes = 6,
                SupportedAxisIndices = new[] { 0, 2, 5 }, Objects = AxisObjects(0, 2, 5),
            });
            AddRow("LeftThumbAxisX", new MappingSource
            {
                DeviceGuid = "", Descriptor = "Axis 0", ParamStickDeadZoneShape = 2, ParamStickDeadZoneInner = 0.1,
            });
            AddRow("ButtonA", Any(SourceCoercion.LeftStickRingDescriptor));
            Array.Fill(wheel.InputState.Axis, 32768);
            wheel.InputState.Axis[2] = wheel.InputState.Axis[5] = 0;
            wheel.InputState.Axis[0] = 65535;

            var gp = Pass(wheel);
            Assert.Equal((short)32767, gp.ThumbLX);
            Assert.True(gp.IsButtonPressed(Gamepad.A));
        }

        [Fact]
        public void ASteeringSource_ReadsOnlyTheAxesItsReaderTakes()
        {
            // A one-axis wheel steering by angle with no Y descriptor. A Steam
            // Circle deadzone left on the row from Direct mode is never read:
            // the steering reader takes Descriptor and ParamYDescriptor alone.
            var wheel = Row(new ShapedDevice { NumAxes = 1, Objects = AxisObjects(0) });
            AddRow("LeftThumbAxisX", new MappingSource
            {
                DeviceGuid = "", Kind = "AngleToAxisX", Descriptor = "Axis 0",
                ParamStickDeadZoneShape = 2, ParamStickDeadZoneInner = 0.1,
            });
            // On a button the same kind falls to the Direct read of
            // Descriptor, and its Y descriptor goes unread.
            AddRow("ButtonA", new MappingSource
            {
                DeviceGuid = "", Kind = "AngleToAxisX", Descriptor = "Axis 0", ParamYDescriptor = "Axis 4",
            });
            // The steering reader takes "Axis N" alone and reads a ring as
            // centered, so a ring stamped as the Y descriptor takes no axis.
            AddRow("RightThumbAxisX", new MappingSource
            {
                DeviceGuid = "", Kind = "AngleToAxisX", Descriptor = "Axis 0",
                ParamYDescriptor = SourceCoercion.RightStickRingDescriptor,
            });
            wheel.InputState.Axis[0] = 65535;

            var gp = Pass(wheel);
            Assert.True(gp.ThumbLX > 32000);
            Assert.True(gp.IsButtonPressed(Gamepad.A));
            Assert.True(gp.ThumbRX > 32000);
        }

        [Fact]
        public void AMotionKindOnAButton_IsReadThroughItsDescriptor()
        {
            // On a button or trigger target the motion kinds fall to the
            // Direct read, which takes the descriptor a kind change left.
            var tp = Touchpad();
            AddRow("ButtonA", new MappingSource
            {
                DeviceGuid = "", Kind = "MotionLeanX", Descriptor = "Axis 3", HalfAxis = true, Invert = true,
            });
            Assert.False(Pass(tp).IsButtonPressed(Gamepad.A));
        }

        [Fact]
        public void ARampedOrIncrementalSource_IsNotHiddenByADescriptorItNeverReads()
        {
            // A kind change keeps the old descriptor in the row. Ramped and
            // Incremental read ParamUp and ParamDown alone.
            var joystick = Row(new ShapedDevice { NumAxes = 2, Objects = AxisObjects(0, 1) });
            var hasAxes = typeof(InputManager).GetMethod("HasAxesFor", BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(UserDevice), typeof(MappingSource), typeof(CustomInputState), typeof(bool) }, null);
            Assert.NotNull(hasAxes);
            foreach (string kind in new[] { "Ramped", "Incremental" })
                foreach (bool stick in new[] { false, true })
                {
                    var src = new MappingSource { DeviceGuid = "", Kind = kind, Descriptor = "Gamepad RightStickX", ParamUp = "Button 0" };
                    Assert.True((bool)hasAxes.Invoke(null, new object[] { joystick, src, joystick.InputState, stick }), kind);
                }
            // The Direct read of the same descriptor does read it.
            var direct = Any("Gamepad RightStickX");
            Assert.False((bool)hasAxes.Invoke(null, new object[] { joystick, direct, joystick.InputState, false }));
        }

        // ── Macro triggers ──────────────────────────────────────────────

        [Fact]
        public void ADeviceFreeDescriptorTrigger_IgnoresADeviceWithoutTheAxes()
        {
            var ring = new MappingSource { Descriptor = SourceCoercion.LeftStickRingDescriptor, DeadZone = 50 };
            var active = typeof(InputManager).GetMethod("AnySlotDeviceDescriptorActive", BindingFlags.NonPublic | BindingFlags.Instance);
            var consume = typeof(InputManager).GetMethod("AddConsumedDescriptor", BindingFlags.NonPublic | BindingFlags.Instance);
            var scratch = (HashSet<(string, string)>[])typeof(InputManager)
                .GetField("_consumedScratchBySlot", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            Assert.NotNull(active);
            Assert.NotNull(consume);
            scratch[Slot]?.Clear();
            try
            {
                Touchpad();
                Assert.False((bool)active.Invoke(new InputManager(), new object[] { Slot, ring }));
                consume.Invoke(new InputManager(), new object[] { Slot, Guid.Empty, ring });
                Assert.True(scratch[Slot] == null || scratch[Slot].Count == 0);

                var pad = Pad();
                pad.InputState.Axis[0] = 65535;
                Assert.True((bool)active.Invoke(new InputManager(), new object[] { Slot, ring }));
            }
            finally
            {
                scratch[Slot]?.Clear();
            }
        }

        // ── Activators ──────────────────────────────────────────────────

        [Fact]
        public void AHoldActivator_ReleasesWhenTheControllerLeavesATouchpadBehind()
        {
            // The left half of the stick, where the touchpad's zeroed Axis 0
            // reads fully pushed.
            var pad = Pad();
            var tp = Touchpad();
            _set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "", Kind = "Axis", Descriptor = "Gamepad LeftStickX",
                AxisThreshold = 0.5, AxisHalf = true, AxisInvert = true, Mode = "Hold", LayerMask = "Lean",
            });
            AddLayerRow("ButtonB", "Lean", Named(tp, "Button 1"));
            tp.InputState.Buttons[1] = true;

            pad.InputState.Axis[0] = 0;
            Pass(pad);
            Assert.True(Pass(tp).IsButtonPressed(Gamepad.B));

            // The pad leaves mid-hold. Nothing left on the slot reads the
            // stick, and the layer lets go.
            pad.IsOnline = false;
            Assert.False(Pass(tp).IsButtonPressed(Gamepad.B));
        }

        [Fact]
        public void AHoldActivator_ReleasesWhenTheControllerLeavesAHeadTrackerBehind()
        {
            // A head tracker never answers the wildcard, so its pass reads
            // nothing. With the pad gone, nothing on the slot can read the
            // stick, and the layer lets go.
            var pad = Pad();
            var tracker = Touchpad();
            tracker.CapType = InputDeviceType.HeadTracker;
            _set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "", Kind = "Axis", Descriptor = "Gamepad LeftStickX",
                AxisThreshold = 0.5, AxisHalf = true, Mode = "Hold", LayerMask = "Lean",
            });
            AddLayerRow("ButtonB", "Lean", Named(tracker, "Button 1"));
            tracker.InputState.Buttons[1] = true;

            pad.InputState.Axis[0] = 65535;
            Pass(pad);
            Assert.True(Pass(tracker).IsButtonPressed(Gamepad.B));

            pad.IsOnline = false;
            Assert.False(Pass(tracker).IsButtonPressed(Gamepad.B));
        }

        [Fact]
        public void AnAnyDeviceActivator_ReadsThePushingDeviceBehindOneWithoutTheAxis()
        {
            // The touchpad comes first on the slot. Its zeroed stick reads as
            // pushed, and picking it over the pad that is pushing left the
            // layer off: the touchpad's own read is released.
            var tp = Touchpad();
            var pad = Pad();
            _set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "", Kind = "Axis", Descriptor = "Gamepad LeftStickX",
                AxisThreshold = 0.5, Mode = "Hold", LayerMask = "Lean",
            });
            AddLayerRow("ButtonB", "Lean", Named(pad, "Button 1"));
            pad.InputState.Buttons[1] = true;

            pad.InputState.Axis[0] = 65535;
            Pass(tp);
            Assert.True(Pass(pad).IsButtonPressed(Gamepad.B));
        }

        private static bool OnCycleLayer(Gamepad gp) => gp.IsButtonPressed(Gamepad.B) || gp.IsButtonPressed(Gamepad.X);

        private void AddCycleRows(UserDevice reader)
        {
            AddLayerRow("ButtonB", "One", Named(reader, "Button 1"));
            AddLayerRow("ButtonX", "Two", Named(reader, "Button 1"));
            reader.InputState.Buttons[1] = true;
        }

        [Fact]
        public void ACyclesPreviousButton_ReadsRestFromADeviceWithoutItsAxes()
        {
            var tp = Touchpad();
            _set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "", Kind = "Button", Descriptor = "Button 0", Mode = "Cycle",
                CycleLayers = "One|Two", CyclePrevDescriptor = SourceCoercion.LeftStickRingDescriptor,
            });
            AddCycleRows(tp);
            for (int i = 0; i < 3; i++) Assert.False(OnCycleLayer(Pass(tp)));
        }

        [Fact]
        public void ACyclesPreviousButton_StepsFromAJoystickWithoutNextsStick()
        {
            // Next reads the right stick's ring, which a two-axis joystick
            // lacks. Its own Previous button still steps the cycle.
            var joystick = Row(new ShapedDevice { NumAxes = 2, Objects = AxisObjects(0, 1) });
            _set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "", Kind = "Button", Descriptor = SourceCoercion.RightStickRingDescriptor, Mode = "Cycle",
                CycleLayers = "One|Two", CyclePrevDescriptor = "Button 5",
            });
            AddCycleRows(joystick);
            Assert.False(OnCycleLayer(Pass(joystick)));
            joystick.InputState.Buttons[5] = true;
            Assert.True(OnCycleLayer(Pass(joystick)));
        }

        [Fact]
        public void AHeldPreviousButton_StepsTheCycleOnce_WithTwoDevicesOnTheSlot()
        {
            // Previous was read from each pass's own device, so a button held
            // on the joystick read down on its pass and up on the pad's, and
            // the queue stepped on every frame it was held.
            var joystick = Row(new ShapedDevice { NumAxes = 2, Objects = AxisObjects(0, 1) });
            var pad = Pad();
            _set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "", Kind = "Axis", Descriptor = "Axis 3", AxisThreshold = 0.5, Mode = "Cycle",
                CycleLayers = "One|Two", CyclePrevDescriptor = "Button 5",
            });
            AddCycleRows(pad);
            joystick.InputState.Buttons[5] = true;

            var seen = new HashSet<bool>();
            for (int frame = 0; frame < 4; frame++)
            {
                Pass(joystick);
                var gp = Pass(pad);
                Assert.True(OnCycleLayer(gp));
                seen.Add(gp.IsButtonPressed(Gamepad.B));
            }
            Assert.Single(seen);
        }

        [Fact]
        public void ACyclesPreviousButtonOnAnotherDevice_StillStepsAfterNextsDeviceLeaves()
        {
            var tp = Touchpad();
            var kb = Keyboard();
            _set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = Guid.NewGuid().ToString(), Kind = "Button", Descriptor = "Button 0", Mode = "Cycle",
                CycleLayers = "One|Two", CyclePrevDeviceGuid = kb.InstanceGuidString, CyclePrevDescriptor = "Button 5",
            });
            AddCycleRows(tp);
            Assert.False(OnCycleLayer(Pass(tp)));
            kb.InputState.Buttons[5] = true;
            Assert.True(OnCycleLayer(Pass(tp)));
        }

        [Fact]
        public void APinnedCycleWhoseDeviceLeft_DoesNotStepOnTheRestState()
        {
            // The release a departed device's activator settles to reads no
            // input, and the all-rest state's zeroed axes read as the ring
            // pushed to the rim.
            var tp = Touchpad();
            _set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = Guid.NewGuid().ToString(), Kind = "Button", Descriptor = "Button 0", Mode = "Cycle",
                CycleLayers = "One|Two", CyclePrevDescriptor = SourceCoercion.LeftStickRingDescriptor,
            });
            AddCycleRows(tp);
            for (int i = 0; i < 3; i++) Assert.False(OnCycleLayer(Pass(tp)));
        }

        [Fact]
        public void ACyclesPreviousButtonOnAnOfflineDevice_ReadsReleased()
        {
            var tp = Touchpad();
            _set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "", Kind = "Button", Descriptor = "Button 0", Mode = "Cycle", CycleLayers = "One|Two",
                CyclePrevDeviceGuid = Guid.NewGuid().ToString(), CyclePrevDescriptor = SourceCoercion.LeftStickRingDescriptor,
            });
            AddCycleRows(tp);
            for (int i = 0; i < 3; i++) Assert.False(OnCycleLayer(Pass(tp)));
        }

        [Fact]
        public void AChordsOfflineSecondHalf_ReadsReleased()
        {
            var pad = Pad();
            _set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = pad.InstanceGuidString, Kind = "Chord", Descriptor = "Button 0",
                ChordSecondDeviceGuid = Guid.NewGuid().ToString(), ChordSecondDescriptor = SourceCoercion.LeftStickRingDescriptor,
                Mode = "Hold", LayerMask = "Lean",
            });
            AddLayerRow("ButtonB", "Lean", Named(pad, "Button 1"));
            pad.InputState.Buttons[0] = pad.InputState.Buttons[1] = true;
            Assert.False(Pass(pad).IsButtonPressed(Gamepad.B));
        }

        // ── Menus and the Web Menus phone ───────────────────────────────

        [Fact]
        public void ADeviceFreeMenusClick_IgnoresADeviceWithoutItsAxes()
        {
            var engineField = typeof(InputService).GetField("_inputManagerStatic", BindingFlags.Static | BindingFlags.NonPublic);
            object engine = engineField.GetValue(null);
            var im = new InputManager();
            try
            {
                engineField.SetValue(null, im);
                // A touchpad-hosted grid whose click is the left stick's ring.
                var menu = new MenuDefinitionEntry
                {
                    MenuId = MenuId, Kind = MenuKind.Grid, CellCount = 4,
                    HostDescriptor = "Touchpad 0", ClickDescriptor = SourceCoercion.LeftStickRingDescriptor,
                    FireType = MenuFireType.Click,
                };
                for (int i = 0; i < 4; i++) menu.Items.Add(new MenuItemDefinition { Index = i, VirtualKey = 0x41 + i });
                _set.Menus.Add(menu);
                var tp = Touchpad();
                tp.InputState.Touchpads = new[] { new TouchpadInputState(2) };
                tp.InputState.Touchpads[0].FingerDown[0] = true;
                tp.InputState.Touchpads[0].FingerX[0] = 0.25f;
                tp.InputState.Touchpads[0].FingerY[0] = 0.25f;

                using (InputManager.EnterMenuPublication())
                    im.UpdateMenuContexts(tp, tp.InputState);
                var state = im.MenuContexts[(Slot, tp.InstanceGuid, MenuId)].State;
                Assert.False(state.Clicked);
                Assert.Equal(-1, state.AssertedIndex);
            }
            finally
            {
                engineField.SetValue(null, engine);
            }
        }

        [Fact]
        public void TheWebMenusPhone_PressesNoCellWhoseGateReadsAStick()
        {
            var phone = Loaded(new WebControllerDevice("menus-" + Guid.NewGuid().ToString("N"), "Web Menus 1",
                layoutKey: WebControllerDevice.MenusLayoutKey));
            phone.InputState = new CustomInputState();
            var standsIn = typeof(InputManager).GetMethod("PhoneStandsIn", BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(MappingSource), typeof(string), typeof(int), typeof(CustomInputState), typeof(bool) }, null);
            Assert.NotNull(standsIn);

            var cell = Any("Menu 3 Item 0");
            Assert.True((bool)standsIn.Invoke(null, new object[] { cell, phone.InstanceGuidString, Slot, phone.InputState, false }));
            cell.Gate2Descriptor = SourceCoercion.LeftStickRingDescriptor;
            Assert.False((bool)standsIn.Invoke(null, new object[] { cell, phone.InstanceGuidString, Slot, phone.InputState, false }));
        }

        // ── The poll path's cost ────────────────────────────────────────

        [Fact]
        public void TheAxisLookup_AllocatesNothingOnARepeat()
        {
            foreach (string d in new[] { SourceCoercion.LeftStickRingDescriptor, "Gamepad LeftStickX", "Axis 3" })
            {
                SourceCoercion.NumberedAxesRead(d, out _, out _);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1000; i++) SourceCoercion.NumberedAxesRead(d, out _, out _);
                Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            }
        }

        // ── Remote Link owners ──────────────────────────────────────────

        [Fact]
        public void AnOwnerReadingItsPadRaw_ShipsTheRawAxes()
        {
            // RemoteLinkStreamTick ships the row's published state, which
            // under Force Raw Joystick Mode holds the joystick's own axes.
            var raw = typeof(InputService).GetMethod("RawModeAxes", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(raw);
            var move = new ShapedDevice
            {
                DeviceType = InputDeviceType.Gamepad, GamepadHandle = (IntPtr)1, NumAxes = 6, RawAxisCount = 2,
                SupportedAxisIndices = new[] { 5 },
            };
            var ud = new UserDevice { ForceRawJoystickMode = true };
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, (int[])raw.Invoke(null, new object[] { ud, move }));
            move.HasExtraGenericAxes = true;
            move.RawAxisCount = 16;
            Assert.Equal(Enumerable.Range(0, 16).ToArray(), (int[])raw.Invoke(null, new object[] { ud, move }));

            ud.ForceRawJoystickMode = false;
            Assert.Null(raw.Invoke(null, new object[] { ud, move }));
            // A joystick opened raw ships its own set.
            ud.ForceRawJoystickMode = true;
            move.GamepadHandle = IntPtr.Zero;
            Assert.Null(raw.Invoke(null, new object[] { ud, move }));

            // And the device list takes it.
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "PadForge.sln"))) d = d.Parent;
            Assert.NotNull(d);
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, "PadForge.App", "Services", "InputService.cs"));
            Assert.Contains("SupportedAxisIndices = RawModeAxes(ud, dev) ?? dev.SupportedAxisIndices,", src);
        }
    }
}
