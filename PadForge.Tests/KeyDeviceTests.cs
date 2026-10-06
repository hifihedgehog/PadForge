using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Each Up, Down and modifier key reads the controller it was picked or
    /// recorded from, the one its picker shows under it. A key used to read
    /// on its source's controller. With RB picked from an Xbox controller as
    /// Up and IR Offscreen picked from a Wii Remote as Down, Down read IR
    /// Offscreen on the Xbox controller, which never sees the sensor bar, so
    /// Down held forever while the picker showed the Wii Remote. A button
    /// picked the same way read the same button number on the wrong pad.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class KeyDeviceTests : IDisposable
    {
        private const int Slot = 4;
        private const string A = "aaaaaaaa-0000-0000-0000-00000000000a";
        private const string B = "bbbbbbbb-0000-0000-0000-00000000000b";
        private const string C = "cccccccc-0000-0000-0000-00000000000c";

        private readonly Func<string, string, int, bool> _savedKeys = SourceCoercion.KeyHeldProvider;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly bool[] _enabled = (bool[])SettingsManager.SlotEnabled.Clone();

        public KeyDeviceTests()
        {
            SourceCoercion.KeyHeldProvider = null;
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            Array.Clear(SettingsManager.SlotCreated);
            Array.Clear(SettingsManager.SlotEnabled);
            SettingsManager.SlotCreated[Slot] = true;
            SettingsManager.SlotEnabled[Slot] = true;
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            InputManager.ClearSourceKindRuntime();
        }

        public void Dispose()
        {
            SourceCoercion.KeyHeldProvider = _savedKeys;
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            InputManager.ClearSourceKindRuntime();
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.SlotMappingSets = _sets;
            Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
            Array.Copy(_enabled, SettingsManager.SlotEnabled, _enabled.Length);
        }

        /// <summary>A Ramp with no attack or release time: +1 while Up reads
        /// held, -1 while Down does, back to 0 with neither.</summary>
        private static MappingSource Ramp(string sourceDevice, string up, string upDevice, string down, string downDevice)
            => new()
            {
                Kind = "Ramped", DeviceGuid = sourceDevice,
                ParamUp = up, ParamUpDeviceGuid = upDevice, ParamDown = down, ParamDownDeviceGuid = downDevice,
                ParamAttackTime = 0, ParamReleaseTime = 0, ParamAutocenter = true,
            };

        /// <summary>The Ramp's value on a stick, read fresh, with
        /// <paramref name="state"/> in hand as <paramref name="evaluatedDevice"/>'s.</summary>
        private static float Read(MappingSource src, CustomInputState state, string evaluatedDevice, int slot = Slot)
            => SourceEvaluator.EvaluateForBipolarAxisTarget(state, src, slot, "LeftThumbAxisX", 0,
                new SourceKindRuntime { FrameSeq = 1 }, 0.016, evaluatedDevice);

        // ── The engine's key read ──────────────────────────────────────

        [Fact]
        public void EachKeyReadsTheControllerItNames()
        {
            var held = new HashSet<(string, string)>();
            var asked = new List<(string Device, string Key, int Slot)>();
            SourceCoercion.KeyHeldProvider = (dev, key, slot) =>
            {
                asked.Add((dev, key, slot));
                return held.Contains((dev, key));
            };
            // RB picked from controller A and Button 1 from controller B, the
            // source read on A's pass with A's state in hand.
            var src = Ramp(A, "Button 5", A, "Button 1", B);
            var stateA = new CustomInputState();

            stateA.Buttons[5] = true;
            Assert.Equal(1f, Read(src, stateA, A));
            Assert.DoesNotContain(asked, a => a.Device == A);

            // A's own Button 1 is not the Down key.
            stateA.Buttons[5] = false;
            stateA.Buttons[1] = true;
            Assert.Equal(0f, Read(src, stateA, A));
            Assert.Contains((B, "Button 1", Slot), asked);

            held.Add((B, "Button 1"));
            Assert.Equal(-1f, Read(src, stateA, A));
        }

        /// <summary>Incremental reads its keys the same way, each on the
        /// controller it names.</summary>
        [Fact]
        public void TheIncrementalKeysReadTheControllersTheyName()
        {
            bool bDown = false;
            SourceCoercion.KeyHeldProvider = (dev, key, slot) => dev == B && key == "Button 1" && bDown;
            var src = new MappingSource
            {
                Kind = "Incremental", DeviceGuid = A, ParamUp = "Button 5", ParamUpDeviceGuid = A,
                ParamDown = "Button 1", ParamDownDeviceGuid = B, ParamRate = 1, ParamMin = 0, ParamMax = 1, ParamSticky = true,
            };
            var runtime = new SourceKindRuntime();
            var stateA = new CustomInputState();
            double Tick()
            {
                runtime.FrameSeq++;
                return runtime.TickIncremental(Slot, "LeftTrigger", 0, src, stateA, 0.25, A);
            }
            stateA.Buttons[5] = true;
            Assert.Equal(0.25, Tick(), 6);
            Assert.Equal(0.5, Tick(), 6);
            stateA.Buttons[5] = false;
            stateA.Buttons[1] = true;
            Assert.Equal(0.5, Tick(), 6);
            bDown = true;
            Assert.Equal(0.25, Tick(), 6);
        }

        [Fact]
        public void AKeySavedWithoutAControllerReadsOnItsSourcesController()
        {
            int asked = 0;
            SourceCoercion.KeyHeldProvider = (dev, key, slot) => { asked++; return false; };
            var src = Ramp(A, "Button 5", null, "Button 1", null);
            var state = new CustomInputState();
            state.Buttons[1] = true;
            Assert.Equal(-1f, Read(src, state, A));
            Assert.Equal(0, asked);
            Assert.Equal(A, SourceKindRuntime.KeyDevice(src.ParamUpDeviceGuid, src));
            Assert.Equal("", SourceKindRuntime.KeyDevice(null, new MappingSource()));
        }

        /// <summary>An "(Any Device)" key of an Incremental or Ramp source is
        /// read once a frame, so it reads the whole slot through the app. The
        /// Invert on Hold mirror reads one on the pass device, as Step 3 reads
        /// the modifier of a row each pass reads apart.</summary>
        [Fact]
        public void AnAnyControllerKeyReadsTheSlotAndAnAnyControllerModifierThePass()
        {
            var asked = new List<(string Device, string Key)>();
            var slotHolds = new HashSet<string>();
            SourceCoercion.KeyHeldProvider = (dev, key, slot) =>
            {
                asked.Add((dev, key));
                return dev.Length == 0 && slotHolds.Contains(key);
            };
            var state = new CustomInputState();
            state.Buttons[5] = true;

            var src = Ramp("", "Button 5", "", "Button 1", "");
            Assert.Equal(0f, Read(src, state, A));
            Assert.Contains(("", "Button 5"), asked);
            slotHolds.Add("Button 5");
            Assert.Equal(1f, Read(src, state, A));

            asked.Clear();
            var ioh = new MappingSource
            {
                Kind = "InvertOnHold", DeviceGuid = "", Descriptor = "Button 0",
                ParamModifier = "Button 1", ParamModifierDeviceGuid = "",
            };
            state.Buttons[0] = true;
            Assert.True(SourceEvaluator.EvaluateForButtonTarget(state, ioh, 50, Slot, "ButtonA", 0, null, 0, A));
            state.Buttons[1] = true;
            Assert.False(SourceEvaluator.EvaluateForButtonTarget(state, ioh, 50, Slot, "ButtonA", 0, null, 0, A));
            Assert.Empty(asked);
        }

        [Fact]
        public void TheInvertOnHoldMirrorReadsItsModifierOnTheControllerItNames()
        {
            bool bHolds = false;
            SourceCoercion.KeyHeldProvider = (dev, key, slot) => dev == B && key == "Button 1" && bHolds;
            var ioh = new MappingSource
            {
                Kind = "InvertOnHold", DeviceGuid = A, Descriptor = "Button 0",
                ParamModifier = "Button 1", ParamModifierDeviceGuid = B,
            };
            var stateA = new CustomInputState();
            stateA.Buttons[0] = true;
            stateA.Buttons[1] = true;
            Assert.True(SourceEvaluator.EvaluateForButtonTarget(stateA, ioh, 50, Slot, "ButtonA", 0, null, 0, A));
            bHolds = true;
            Assert.False(SourceEvaluator.EvaluateForButtonTarget(stateA, ioh, 50, Slot, "ButtonA", 0, null, 0, A));
        }

        [Fact]
        public void WithoutTheAppOrASlotAKeyReadsTheStateInHand()
        {
            var state = new CustomInputState();
            state.Buttons[1] = true;
            Assert.Equal(-1f, Read(Ramp(A, "Button 5", A, "Button 1", B), state, A));

            SourceCoercion.KeyHeldProvider = (dev, key, slot) => false;
            Assert.Equal(-1f, Read(Ramp("", "Button 5", "", "Button 1", ""), state, A, slot: -1));
        }

        // ── The saved fields ───────────────────────────────────────────

        [Fact]
        public void TheSettingsFileKeepsAKeysControllerAndLeavesOutOneItNeverHad()
        {
            var ser = new XmlSerializer(typeof(MappingSource));
            var saved = new MappingSource
            {
                Kind = "Ramped", ParamUp = "Button 5", ParamUpDeviceGuid = A,
                ParamDown = "Gamepad ButtonA", ParamDownDeviceGuid = "", ParamModifier = "Button 2",
            };
            var writer = new StringWriter();
            ser.Serialize(writer, saved);
            string xml = writer.ToString();
            Assert.Contains("ParamUpDeviceGuid=\"" + A + "\"", xml);
            Assert.Contains("ParamDownDeviceGuid=\"\"", xml);
            Assert.DoesNotContain("ParamModifierDeviceGuid", xml);

            var back = (MappingSource)ser.Deserialize(new StringReader(xml));
            Assert.Equal(A, back.ParamUpDeviceGuid);
            Assert.Equal("", back.ParamDownDeviceGuid);
            Assert.Null(back.ParamModifierDeviceGuid);

            var old = (MappingSource)ser.Deserialize(new StringReader(
                "<MappingSource Kind=\"Incremental\" DeviceGuid=\"" + A + "\" ParamUp=\"Button 5\" />"));
            Assert.Null(old.ParamUpDeviceGuid);
            Assert.Equal(A, SourceKindRuntime.KeyDevice(old.ParamUpDeviceGuid, old));

            var copy = saved.Clone();
            Assert.Equal(A, copy.ParamUpDeviceGuid);
            Assert.Equal("", copy.ParamDownDeviceGuid);
        }

        [Fact]
        public void RetargetingMovesEachKeyAndDropsOneWithNoCounterpart()
        {
            var src = Ramp("", "Button 5", A, "Button 1", B);
            Assert.False(src.RetargetKeyDevices(g => g == A ? C : null));
            Assert.Equal(("Button 5", C), (src.ParamUp, src.ParamUpDeviceGuid));
            Assert.Equal(("", (string)null), (src.ParamDown, src.ParamDownDeviceGuid));
            Assert.True(src.RetargetKeyDevices(g => null));
            Assert.Equal("", src.ParamUp);

            // A key with no controller of its own, or any controller, stays.
            var kept = Ramp(A, "Button 5", null, "Button 1", "");
            Assert.False(kept.RetargetKeyDevices(g => null));
            Assert.Equal(("Button 5", (string)null), (kept.ParamUp, kept.ParamUpDeviceGuid));
            Assert.Equal(("Button 1", ""), (kept.ParamDown, kept.ParamDownDeviceGuid));

            // An Invert on Hold source reads its modifier alone.
            var ioh = new MappingSource
            {
                Kind = "InvertOnHold", ParamModifier = "Button 2", ParamModifierDeviceGuid = A,
                ParamUp = "Button 9", ParamUpDeviceGuid = B,
            };
            Assert.True(ioh.RetargetKeyDevices(g => g == B ? g : null));
            var direct = new MappingSource { Kind = "Direct", DeviceGuid = A, Descriptor = "Button 0", ParamUp = "Button 9", ParamUpDeviceGuid = A };
            Assert.False(direct.RetargetKeyDevices(g => null));
        }

        // ── Step 3 ─────────────────────────────────────────────────────

        private static UserDevice Device(string guid, int capType = InputDeviceType.Gamepad)
        {
            var id = Guid.Parse(guid);
            var ud = new UserDevice
            {
                InstanceGuid = id, ProductGuid = id, IsOnline = true,
                ProductName = "Pad " + guid.Substring(0, 4), InstanceName = "Pad " + guid.Substring(0, 4),
                CapType = capType, CapButtonCount = 16, InputState = new CustomInputState(),
            };
            Array.Fill(ud.InputState.Axis, 32768);
            SettingsManager.UserDevices.Items.Add(ud);
            var setting = new UserSetting { InstanceGuid = id, ProductGuid = id, MapTo = Slot };
            setting.SetPadSetting(new PadSetting());
            SettingsManager.UserSettings.Items.Add(setting);
            return ud;
        }

        private static MappingSet Set(string target, params MappingSource[] sources)
        {
            var set = new MappingSet();
            set.Rows.Add(new MappingRow { Target = target, LayerMask = "Base", Sources = sources.ToList() });
            SettingsManager.SlotMappingSets[Slot] = set;
            return set;
        }

        private static readonly MethodInfo BeginFrame = typeof(InputManager)
            .GetMethod("BeginFrameMultiSourceTracking", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo Apply = typeof(InputManager)
            .GetMethod("ApplyMappingSetToGamepad", BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly FieldInfo FrameDelta = typeof(InputManager)
            .GetField("_currentFrameDelta", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>One frame: Step 3's pass for each online device, merged
        /// as Step 4 merges them, the larger deflection or pull winning.</summary>
        private static Gamepad Frame(MappingSet set, params UserDevice[] devices)
            => TimedFrame(set, 0, devices);

        /// <summary><see cref="Frame"/> with the frame time set, for the
        /// kinds that move with it.</summary>
        private static Gamepad TimedFrame(MappingSet set, double dt, params UserDevice[] devices)
        {
            BeginFrame.Invoke(null, null);
            if (dt > 0) ((double[])FrameDelta.GetValue(null))[Slot] = dt;
            var merged = new Gamepad();
            foreach (var ud in devices)
            {
                if (!ud.IsOnline) continue;
                object[] args = { ud.InputState, set, ud.InstanceGuidString, 50, Slot, new Gamepad() };
                Apply.Invoke(null, args);
                var gp = (Gamepad)args[5];
                if (Math.Abs((int)gp.ThumbLX) > Math.Abs((int)merged.ThumbLX)) merged.ThumbLX = gp.ThumbLX;
                if (gp.LeftTrigger > merged.LeftTrigger) merged.LeftTrigger = gp.LeftTrigger;
                if (gp.IsButtonPressed(Gamepad.A)) merged.SetButton(Gamepad.A, true);
            }
            return merged;
        }

        private static void Release(params UserDevice[] devices)
        {
            foreach (var ud in devices) Array.Clear(ud.InputState.Buttons);
        }

        /// <summary>The report's case, through Step 3. Up is RB on controller
        /// A and Down is Button 1 on controller B. A's own Button 1 moves
        /// nothing, whether the source names A, as one saved before the fix
        /// does, or no controller, as it does now that its keys read two.</summary>
        [Fact]
        public void TheDownKeyReadsTheControllerItWasPickedFrom()
        {
            SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            var a = Device(A);
            var b = Device(B);
            foreach (string sourceDevice in new[] { A, "" })
            {
                InputManager.ClearSourceKindRuntime();
                var set = Set("LeftThumbAxisX", Ramp(sourceDevice, "Button 5", A, "Button 1", B));
                Release(a, b);
                a.InputState.Buttons[5] = true;
                Assert.True(Frame(set, a, b).ThumbLX > 30000, sourceDevice);
                Release(a, b);
                a.InputState.Buttons[1] = true;
                Assert.Equal(0, Frame(set, a, b).ThumbLX);
                b.InputState.Buttons[1] = true;
                Assert.True(Frame(set, a, b).ThumbLX < -30000, sourceDevice);
            }
        }

        [Fact]
        public void AKeyWorksWhileTheOtherKeysControllerIsOff()
        {
            SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            var a = Device(A);
            var b = Device(B);
            var set = Set("LeftThumbAxisX", Ramp("", "Button 5", A, "Button 1", B));
            a.IsOnline = false;
            a.InputState.Buttons[5] = true; // held when A went away: an offline device reads released
            b.InputState.Buttons[1] = true;
            Assert.True(Frame(set, a, b).ThumbLX < -30000);
            Assert.False(InputManager.KeyHeld(A, "Button 5", Slot));
            Assert.True(InputManager.KeyHeld(B, "Button 1", Slot));
        }

        /// <summary>Keys on two controllers that do not answer "(Any
        /// Device)" leave the source naming none, and every pass reads it on
        /// a slot where no device answers.</summary>
        [Fact]
        public void AKeySourceIsReadOnASlotNoDeviceAnswersFor()
        {
            SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            var left = Device(A, InputDeviceType.VrController);
            var right = Device(B, InputDeviceType.VrController);
            var set = Set("LeftThumbAxisX", Ramp("", "Button 5", A, "Button 1", B));
            right.InputState.Buttons[1] = true;
            Assert.True(Frame(set, left, right).ThumbLX < -30000);
            Release(right);
            Frame(set, left, right);
            left.InputState.Buttons[5] = true;
            Assert.True(Frame(set, left, right).ThumbLX > 30000);
        }

        /// <summary>An "(Any Device)" key reads every controller on the slot
        /// that answers the wildcard and has its input, so a keyboard's own
        /// Button 0 is no "Gamepad A".</summary>
        [Fact]
        public void AnAnyControllerKeyReadsEveryControllerThatHasIt()
        {
            SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            var pad = Device(A);
            var keyboard = Device(B, InputDeviceType.Keyboard);
            var tracker = Device(C, InputDeviceType.VrController);
            var set = Set("LeftThumbAxisX", Ramp("", "Gamepad ButtonA", "", "Button 1", ""));
            keyboard.InputState.Buttons[0] = true;
            Assert.Equal(0, Frame(set, pad, keyboard).ThumbLX);
            // A controller that never answers "(Any Device)" is no part of it.
            tracker.InputState.Buttons[1] = true;
            Assert.Equal(0, Frame(set, pad, keyboard, tracker).ThumbLX);
            Release(tracker);
            pad.InputState.Buttons[0] = true;
            Assert.True(Frame(set, pad, keyboard).ThumbLX > 30000);
            Release(pad, keyboard);
            Frame(set, pad, keyboard);
            keyboard.InputState.Buttons[1] = true;
            Assert.True(Frame(set, pad, keyboard).ThumbLX < -30000);
            Release(keyboard);
            Frame(set, pad, keyboard);
            pad.InputState.Buttons[1] = true;
            Assert.True(Frame(set, pad, keyboard).ThumbLX < -30000);
        }

        /// <summary>A modifier reads the controller it names, the source it
        /// sits on naming another.</summary>
        [Fact]
        public void TheModifierReadsTheControllerItNames()
        {
            SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            var a = Device(A);
            var b = Device(B);
            var set = Set("LeftTrigger",
                new MappingSource { Kind = "Direct", DeviceGuid = A, Descriptor = "Button 3" },
                new MappingSource
                {
                    Kind = "InvertOnHold", DeviceGuid = A, Descriptor = "Button 3",
                    ParamModifier = "Button 1", ParamModifierDeviceGuid = B,
                });
            a.InputState.Buttons[3] = true;
            Assert.Equal(ushort.MaxValue, Frame(set, a, b).LeftTrigger);
            a.InputState.Buttons[1] = true;
            Assert.Equal(ushort.MaxValue, Frame(set, a, b).LeftTrigger);
            b.InputState.Buttons[1] = true;
            Assert.Equal(0, Frame(set, a, b).LeftTrigger);
            // A modifier saved before keys carried a controller reads on its
            // source's, A.
            set.Rows[0].Sources[1].ParamModifierDeviceGuid = null;
            b.InputState.Buttons[1] = false;
            Assert.Equal(0, Frame(set, a, b).LeftTrigger);
            a.InputState.Buttons[1] = false;
            Assert.Equal(ushort.MaxValue, Frame(set, a, b).LeftTrigger);
        }

        /// <summary>A row whose only source reads keys reads the same on every
        /// pass, so an "(Any Device)" modifier on it is read across the slot,
        /// and every pass inverts it alike. Read on each pass's own device,
        /// the passes disagreed and the merge kept the uninverted full
        /// deflection.</summary>
        [Fact]
        public void AKeySourcesRowReadsAnAnyControllerModifierAcrossTheSlot()
        {
            SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            var a = Device(A);
            var b = Device(B);
            var set = Set("LeftThumbAxisX",
                Ramp("", "Button 5", A, "Button 6", A),
                new MappingSource
                {
                    Kind = "InvertOnHold", DeviceGuid = "", ParamModifier = "Button 1", ParamModifierDeviceGuid = "",
                });
            a.InputState.Buttons[6] = true;
            Assert.True(Frame(set, a, b).ThumbLX < -30000);
            b.InputState.Buttons[1] = true;
            Assert.True(Frame(set, a, b).ThumbLX > 30000);
        }

        /// <summary>The rows read once a frame (the multi-source builders and
        /// a stick trim) read a key source on the pass in hand, a slot no
        /// controller answers "(Any Device)" for included.</summary>
        [Fact]
        public void TheRowsReadOnceAFrameReadAKeySourceOnASlotNoDeviceAnswersFor()
        {
            SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            var left = Device(A, InputDeviceType.VrController);
            var right = Device(B, InputDeviceType.VrController);
            var other = new MappingSource { Kind = "Direct", DeviceGuid = A, Descriptor = "Button 9" };

            var trigger = Set("LeftTrigger", Ramp("", "Button 5", A, "Button 1", B), other);
            left.InputState.Buttons[5] = true;
            Assert.Equal(ushort.MaxValue, Frame(trigger, left, right).LeftTrigger);
            Release(left, right);
            InputManager.ClearSourceKindRuntime();

            var stick = Set("LeftThumbAxisX", Ramp("", "Button 5", A, "Button 1", B), other);
            right.InputState.Buttons[1] = true;
            Assert.True(Frame(stick, left, right).ThumbLX < -30000);
            Release(left, right);
            InputManager.ClearSourceKindRuntime();

            var button = Set("ButtonA", new MappingSource
            {
                Kind = "Incremental", DeviceGuid = "", ParamUp = "Button 5", ParamUpDeviceGuid = A,
                ParamDown = "Button 1", ParamDownDeviceGuid = B, ParamRate = 1, ParamMin = 0, ParamMax = 1, ParamSticky = true,
            }, other);
            left.InputState.Buttons[5] = true;
            for (int i = 0; i < 3; i++) TimedFrame(button, 0.25, left, right);
            Assert.True(TimedFrame(button, 0.25, left, right).IsButtonPressed(Gamepad.A));
            Release(left, right);
            InputManager.ClearSourceKindRuntime();

            var gate = Set("LeftTrigger", Ramp("", "Button 5", A, "Button 1", B),
                new MappingSource { Kind = "Direct", DeviceGuid = A, Descriptor = "Axis 0" });
            gate.Rows[0].CombineMode = "StickTrim";
            left.InputState.Buttons[5] = true;
            Assert.Equal(ushort.MaxValue, TimedFrame(gate, 0.1, left, right).LeftTrigger);
            Release(left, right);
            InputManager.ClearSourceKindRuntime();

            // A positive trim lowers the level (AdvanceStickTrimLevel).
            var trim = Set("LeftTrigger", other, Ramp("", "Button 5", B, "Button 1", A));
            trim.Rows[0].CombineMode = "StickTrim";
            left.InputState.Buttons[9] = true;
            right.InputState.Buttons[5] = true;
            TimedFrame(trim, 0.25, left, right);
            Assert.True(TimedFrame(trim, 0.25, left, right).LeftTrigger < 60000);
        }

        /// <summary>An "(Any Device)" menu cell key reads as the slot's, so a
        /// menu that answers for any controller, a Web Menus phone's
        /// included, presses it.</summary>
        [Fact]
        public void AnAnyControllerMenuCellKeyReadsAsTheSlots()
        {
            var savedMenu = SourceCoercion.MenuItemFiredProvider;
            try
            {
                Device(A);
                SourceCoercion.MenuItemFiredProvider = (slot, guid, menu, item) => slot == Slot && guid == "" && menu == 3 && item == 1;
                Assert.True(InputManager.KeyHeld("", "Menu 3 Item 1", Slot));
                Assert.False(InputManager.KeyHeld("", "Menu 3 Item 2", Slot));
            }
            finally { SourceCoercion.MenuItemFiredProvider = savedMenu; }
        }

        // ── The pickers ────────────────────────────────────────────────

        private sealed class TwoControllers
        {
            public PadViewModel Pad;
            public UserDevice Xbox, Wii;
            public InputService Service;
            public MappingItem Row(string target) => Pad.Mappings.Single(m => m.TargetSettingName == target);
        }

        /// <summary>A slot with an Xbox controller (A) and a Wii Remote (B),
        /// its picker lists built the way the app builds them.</summary>
        private static TwoControllers Populate()
        {
            var xbox = new UserDevice
            {
                InstanceGuid = Guid.Parse(A), ProductGuid = Guid.Parse(A), IsOnline = true,
                ProductName = "Xbox Controller", InstanceName = "Xbox Controller", CapType = InputDeviceType.Gamepad,
                CapButtonCount = 16, CapAxeCount = 6, CapPovCount = 1, InputState = new CustomInputState(),
            };
            var wii = new UserDevice
            {
                InstanceGuid = Guid.Parse(B), ProductGuid = Guid.Parse(B), IsOnline = true,
                ProductName = "Nintendo Wii Remote", InstanceName = "Nintendo Wii Remote", CapType = InputDeviceType.Gamepad,
                VendorId = 0x057E, ProdId = 0x0306, CapButtonCount = 11, CapPovCount = 1, HasAccel = true,
                InputState = new CustomInputState(),
            };
            SettingsManager.UserDevices.Items.Add(xbox);
            SettingsManager.UserDevices.Items.Add(wii);
            var vm = new MainViewModel();
            var svc = new InputService(vm) { SettingsService = new SettingsService(vm) };
            var pad = vm.Pads[0];
            pad.OutputType = VirtualControllerType.PlayStation;
            pad.MappedDevices.Add(new PadViewModel.MappedDeviceInfo { InstanceGuid = xbox.InstanceGuid, Name = "Xbox Controller", IsOnline = true });
            pad.MappedDevices.Add(new PadViewModel.MappedDeviceInfo { InstanceGuid = wii.InstanceGuid, Name = "Nintendo Wii Remote", IsOnline = true });
            typeof(InputService).GetMethod("PopulateAvailableInputs", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(svc, new object[] { pad, null });
            return new TwoControllers { Pad = pad, Xbox = xbox, Wii = wii, Service = svc };
        }

        private static InputChoice Key(MappingItem row, string descriptor, string device)
            => row.KeyInputs.First(c => c.Descriptor == descriptor && c.DeviceGuid == device);

        [Fact]
        public void PickingAKeyStoresTheControllerItWasPickedFrom()
        {
            var p = Populate();
            var row = p.Row("LeftTrigger");
            var src = row.PrimaryKindSource;
            src.Kind = "Incremental";

            src.ParamUpInputChoice = Key(row, "Button 5", A);
            Assert.Equal(A, src.ParamUpDeviceGuid);
            Assert.Equal(A, src.DeviceGuid);

            var offscreen = Key(row, "IR Offscreen", B);
            src.ParamDownInputChoice = offscreen;
            Assert.Equal(B, src.ParamDownDeviceGuid);
            Assert.Equal(A, src.ParamUpDeviceGuid);
            Assert.Equal("", src.DeviceGuid);
            Assert.Same(offscreen, src.ParamDownInputChoice);
            Assert.Equal(offscreen.DeviceLabel, src.ParamDownDeviceLabel);

            var saved = src.ToDomain();
            Assert.Equal((A, B, ""), (saved.ParamUpDeviceGuid, saved.ParamDownDeviceGuid, saved.DeviceGuid));
            Assert.Equal(B, SourceKindRuntime.KeyDevice(saved.ParamDownDeviceGuid, saved));
            var loaded = MappingSourceItem.FromDomain(saved);
            Assert.Equal((A, B), (loaded.ParamUpDeviceGuid, loaded.ParamDownDeviceGuid));

            // The modifier of an Invert on Hold source, picked from the full
            // list, carries its controller the same way.
            var ioh = new MappingSourceItem { Kind = "InvertOnHold" };
            row.ExtraSources.Add(ioh);
            var three = row.ParamInputs.First(c => c.Descriptor == "Button 3" && c.DeviceGuid == B);
            ioh.ParamModifierInputChoice = three;
            Assert.Equal(B, ioh.ParamModifierDeviceGuid);
            Assert.Same(three, ioh.ParamModifierInputChoice);
            Assert.Equal(B, ioh.ToDomain().ParamModifierDeviceGuid);
            Assert.Equal(B, MappingSourceItem.FromDomain(ioh.ToDomain()).ParamModifierDeviceGuid);
        }

        [Fact]
        public void PickingTheSameInputFromAnotherControllerMovesTheKey()
        {
            var p = Populate();
            var row = p.Row("LeftTrigger");
            var src = row.PrimaryKindSource;
            src.Kind = "Ramped";
            src.ParamUpInputChoice = Key(row, "Button 1", A);
            Assert.Equal(A, src.ParamUpDeviceGuid);
            var wiiOne = Key(row, "Button 1", B);
            src.ParamUpInputChoice = wiiOne;
            Assert.Equal(B, src.ParamUpDeviceGuid);
            Assert.Same(wiiOne, src.ParamUpInputChoice);
            Assert.Equal(B, src.DeviceGuid);
            // With Down on A the source names no controller, and Up still
            // shows the Wii Remote's Button 1, not the first one listed.
            src.ParamDownInputChoice = Key(row, "Button 2", A);
            Assert.Equal("", src.DeviceGuid);
            Assert.Same(wiiOne, src.ParamUpInputChoice);
        }

        /// <summary>A key saved before keys carried a controller takes the
        /// one its picker shows. IR Offscreen on a source saved with the Xbox
        /// controller showed the Wii Remote, the one controller that lists
        /// it, and now reads it.</summary>
        [Fact]
        public void AKeySavedWithoutAControllerTakesTheOneItsPickerShows()
        {
            var p = Populate();
            var row = p.Row("LeftTrigger");
            row.LoadPrimaryKind(new MappingSource
            {
                Kind = "Incremental", DeviceGuid = A, ParamUp = "Button 5", ParamDown = "IR Offscreen",
            });
            var src = row.PrimaryKindSource;
            Assert.Equal(A, src.ParamUpDeviceGuid);
            Assert.Equal(B, src.ParamDownDeviceGuid);
            Assert.Equal("", src.DeviceGuid);

            // A key no listed controller offers keeps reading on the source's.
            row.LoadPrimaryKind(new MappingSource { Kind = "Incremental", DeviceGuid = A, ParamUp = "Button 200" });
            Assert.Null(row.PrimaryKindSource.ParamUpDeviceGuid);
            Assert.Equal(A, row.PrimaryKindSource.DeviceGuid);

            // A key saved with its controller loads with it.
            row.LoadPrimaryKind(new MappingSource
            {
                Kind = "Ramped", DeviceGuid = "", ParamUp = "Button 1", ParamUpDeviceGuid = B,
                ParamDown = "Button 2", ParamDownDeviceGuid = A,
            });
            Assert.Equal((B, A), (row.PrimaryKindSource.ParamUpDeviceGuid, row.PrimaryKindSource.ParamDownDeviceGuid));
            row.LoadPrimaryKind(new MappingSource
            {
                Kind = "InvertOnHold", DeviceGuid = A, ParamModifier = "Button 3", ParamModifierDeviceGuid = B,
            });
            Assert.Equal(B, row.PrimaryKindSource.ParamModifierDeviceGuid);
            row.LoadPrimaryKind(new MappingSource { Kind = "Direct", DeviceGuid = A, Descriptor = "Button 0" });
            Assert.Null(row.PrimaryKindSource.ParamModifierDeviceGuid);
        }

        /// <summary>A load sets the source's device before its keys. A key
        /// left without a controller by the row loaded before took one then,
        /// read against the new device, and the source followed it away from
        /// the device the new row names.</summary>
        [Fact]
        public void ALoadAdoptsNothingFromTheRowLoadedBefore()
        {
            var p = Populate();
            var row = p.Row("LeftTrigger");
            var src = row.PrimaryKindSource;
            // Keys with no controller yet: Button 15 only the Xbox controller
            // lists, IR Offscreen only the Wii Remote.
            src.LoadingKeys = true;
            src.Kind = "Incremental";
            src.DeviceGuid = A;
            src.ParamUp = "Button 15";
            src.ParamDown = "IR Offscreen";
            src.LoadingKeys = false;

            row.LoadPrimaryKind(new MappingSource
            {
                Kind = "Incremental", DeviceGuid = B, ParamUp = "Button 1", ParamUpDeviceGuid = B,
                ParamDown = "Button 2", ParamDownDeviceGuid = B,
            });
            Assert.Equal((B, B, B), (src.DeviceGuid, src.ParamUpDeviceGuid, src.ParamDownDeviceGuid));
        }

        [Fact]
        public void ResettingAKeyClearsItsControllerAndTheSourceFollowsTheOther()
        {
            var p = Populate();
            var row = p.Row("LeftTrigger");
            var src = row.PrimaryKindSource;
            src.Kind = "Incremental";
            src.ParamUpInputChoice = Key(row, "Button 5", A);
            src.ParamDownInputChoice = Key(row, "Button 1", B);
            Assert.Equal("", src.DeviceGuid);
            src.ResetSettingCommand.Execute(nameof(MappingSourceItem.ParamUpInputChoice));
            Assert.Equal("", src.ParamUp);
            Assert.Null(src.ParamUpDeviceGuid);
            Assert.Equal(B, src.DeviceGuid);
            Assert.Null(src.ToDomain().ParamUpDeviceGuid);

            // An empty key saves no controller, whatever is left beside it.
            Assert.Null(new MappingSourceItem { ParamUpDeviceGuid = A, ParamModifierDeviceGuid = B }.ToDomain().ParamUpDeviceGuid);
            Assert.Null(new MappingSourceItem { ParamModifierDeviceGuid = B }.ToDomain().ParamModifierDeviceGuid);

            // Clear empties the keys with their controllers.
            src.ParamUpInputChoice = Key(row, "Button 5", A);
            row.ClearCommand.Execute(null);
            Assert.Equal(((string)null, (string)null), (src.ParamUpDeviceGuid, src.ParamDownDeviceGuid));
            Assert.Null(src.ParamModifierDeviceGuid);
        }

        // ── Record ─────────────────────────────────────────────────────

        private static readonly MethodInfo PollTick = typeof(RecorderService)
            .GetMethod("PollTick", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>A key recorded on a second controller lands with that
        /// controller and leaves the other key where it was. Recording used to
        /// stamp the firing device on the source, which moved the Up key to
        /// the controller Down was recorded on.</summary>
        [Fact]
        public void RecordingAKeyStoresTheControllerItFiredOn()
        {
            var p = Populate();
            var vm = new MainViewModel();
            var recorder = new RecorderService(vm);
            var pad = vm.Pads[0];
            pad.OutputType = VirtualControllerType.PlayStation;
            pad.MappedDevices.Add(new PadViewModel.MappedDeviceInfo { InstanceGuid = p.Xbox.InstanceGuid, Name = "Xbox Controller", IsOnline = true });
            pad.MappedDevices.Add(new PadViewModel.MappedDeviceInfo { InstanceGuid = p.Wii.InstanceGuid, Name = "Nintendo Wii Remote", IsOnline = true });
            var row = pad.Mappings.Single(m => m.TargetSettingName == "LeftTrigger");
            var src = new MappingSourceItem { Kind = "Incremental" };
            row.ExtraSources.Add(src);
            src.SetParamKey(MappingSourceItem.ParamRecordTarget.Up, "Button 5", A);
            Assert.Equal(A, src.DeviceGuid);

            recorder.StartRecordingExtraSourceParam(row, src, 0, RecorderService.ParamTarget.Down);
            Assert.True(recorder.IsRecording);
            p.Wii.InputState.Buttons[2] = true;
            try { PollTick.Invoke(recorder, new object[] { null, EventArgs.Empty }); }
            finally { if (recorder.IsRecording) recorder.CancelRecording(); }
            Assert.Equal(("Button 2", B), (src.ParamDown, src.ParamDownDeviceGuid));
            Assert.Equal(("Button 5", A), (src.ParamUp, src.ParamUpDeviceGuid));
            Assert.Equal("", src.DeviceGuid);
        }

        /// <summary>A key recorded through the literal-descriptor path, a MIDI
        /// note here, lands with the controller it fired on too.</summary>
        [Fact]
        public void AMidiKeyRecordedLandsWithItsController()
        {
            var midi = new UserDevice
            {
                InstanceGuid = Guid.Parse(C), ProductName = "MIDI Keys", IsOnline = true,
                CapType = InputDeviceType.Midi, InputState = new CustomInputState { Midi = new MidiInputState() },
            };
            Array.Fill(midi.InputState.Axis, 32768);
            SettingsManager.UserDevices.Items.Add(midi);
            var vm = new MainViewModel();
            var recorder = new RecorderService(vm);
            var pad = vm.Pads[0];
            pad.OutputType = VirtualControllerType.PlayStation;
            pad.MappedDevices.Add(new PadViewModel.MappedDeviceInfo { InstanceGuid = midi.InstanceGuid, Name = "MIDI Keys", IsOnline = true });
            var row = pad.Mappings.Single(m => m.TargetSettingName == "LeftTrigger");
            var src = new MappingSourceItem { Kind = "Incremental" };
            row.ExtraSources.Add(src);
            recorder.StartRecordingExtraSourceParam(row, src, 0, RecorderService.ParamTarget.Up);
            Assert.True(recorder.IsRecording);
            midi.InputState.Midi.Notes[60] = true;
            try { PollTick.Invoke(recorder, new object[] { null, EventArgs.Empty }); }
            finally { if (recorder.IsRecording) recorder.CancelRecording(); }
            Assert.Equal(("Midi Note 60", C), (src.ParamUp, src.ParamUpDeviceGuid));
        }

        // ── Where a source's controller moves, its keys' move ──────────

        [Fact]
        public void ACopyMovesEachKeysControllerAndDropsOneWithNoCounterpart()
        {
            var row = new MappingRow
            {
                Target = "LeftThumbAxisX",
                Sources = new List<MappingSource>
                {
                    Ramp("", "Button 5", A, "Button 1", B),
                    Ramp("", "Button 6", B, "", null),
                },
            };
            string To(string g) => g == A ? C : g == B ? null : g;
            var copied = InputService.CopyRowSources(row, To, out _);
            var only = Assert.Single(copied);
            Assert.Equal(("Button 5", C), (only.ParamUp, only.ParamUpDeviceGuid));
            Assert.Equal("", only.ParamDown);
            Assert.Equal(B, row.Sources[0].ParamDownDeviceGuid);

            row.CombineMode = "Custom";
            var custom = InputService.CopyRowSources(row, To, out _);
            Assert.Equal(2, custom.Count);
            Assert.True(SourceEvaluator.IsUnmappedDirect(custom[1]));
        }

        /// <summary>A copy of one controller's rows onto another moves the
        /// keys that read the copied controller, and leaves a key on a third
        /// where it was.</summary>
        [Fact]
        public void ADeviceCopyMovesTheKeysOnTheCopiedController()
        {
            SettingsManager.SlotMappingSets[Slot] = new MappingSet();
            var rows = new List<MappingRow>
            {
                new() { Target = "LeftThumbAxisX", LayerMask = "Base", Sources = new List<MappingSource> { Ramp(A, "Button 5", A, "Button 1", C) } },
            };
            InputService.ApplyMultiSourceRowsToCurrentDevice(Slot, Guid.Parse(B), rows);
            var copied = Assert.Single(SettingsManager.SlotMappingSets[Slot].Rows.Single(r => r.Target == "LeftThumbAxisX").Sources);
            Assert.Equal((B, B, C), (copied.DeviceGuid, copied.ParamUpDeviceGuid, copied.ParamDownDeviceGuid));
            Assert.Equal(A, rows[0].Sources[0].ParamUpDeviceGuid);
        }

        [Fact]
        public void ARekeyedControllerTakesItsKeysAlong()
        {
            var set = Set("LeftThumbAxisX",
                Ramp(A, "Button 5", A, "Button 1", B),
                new MappingSource { Kind = "InvertOnHold", ParamModifier = "Button 2", ParamModifierDeviceGuid = B });
            InputService.RemapDeviceGuidsInSlotMappingSets(new Dictionary<string, string> { [B] = C });
            var keys = set.Rows[0].Sources;
            Assert.Equal((A, A, C), (keys[0].DeviceGuid, keys[0].ParamUpDeviceGuid, keys[0].ParamDownDeviceGuid));
            Assert.Equal(C, keys[1].ParamModifierDeviceGuid);
        }

        [Fact]
        public void UnassigningAControllerDropsItsKeys()
        {
            var set = Set("LeftThumbAxisX",
                Ramp("", "Button 5", A, "Button 1", B),
                new MappingSource { Kind = "Direct", DeviceGuid = A, Descriptor = "Axis 0" });
            set.Rows.Add(new MappingRow
            {
                Target = "LeftThumbAxisY", LayerMask = "Base",
                Sources = new List<MappingSource> { Ramp("", "Button 6", B, "Button 7", B) },
            });
            SettingsService.StripDeviceFromSlot(Guid.Parse(B), Slot);
            var x = set.Rows.Single(r => r.Target == "LeftThumbAxisX").Sources;
            Assert.Equal(2, x.Count);
            Assert.Equal(("Button 5", A), (x[0].ParamUp, x[0].ParamUpDeviceGuid));
            Assert.Equal(("", (string)null), (x[0].ParamDown, x[0].ParamDownDeviceGuid));
            Assert.DoesNotContain(set.Rows, r => r.Target == "LeftThumbAxisY");
        }

        /// <summary>Two sources that differ only in a key's controller are
        /// two reads, and the load keeps both.</summary>
        [Fact]
        public void TheLoadKeepsTwoSourcesThatDifferOnlyInAKeysController()
        {
            var set = Set("LeftThumbAxisX",
                Ramp("", "Button 5", A, "Button 1", A),
                Ramp("", "Button 5", A, "Button 1", B),
                Ramp("", "Button 5", A, "Button 1", B));
            SettingsService.SanitizeMappingSet(set, Slot);
            var kept = set.Rows[0].Sources;
            Assert.Equal(2, kept.Count);
            Assert.Equal(A, kept[0].ParamDownDeviceGuid);
            Assert.Equal(B, kept[1].ParamDownDeviceGuid);
        }

        /// <summary>The device merge drops a key whose controller left the
        /// slot, and counts the controllers a key reads as authored, so it
        /// adds no default source for them. A key that reads any controller
        /// covers them all.</summary>
        [Fact]
        public void TheDeviceMergeTreatsAKeysControllerAsItsSources()
        {
            var savedHook = SettingsService.AfterMappingSetsRefreshed;
            try
            {
                SettingsService.AfterMappingSetsRefreshed = null;
                var pad = Device(A);
                var setting = SettingsManager.FindSettingByInstanceGuidAndSlot(pad.InstanceGuid, Slot);
                setting.SetPadSetting(SettingsManager.CreateDefaultPadSetting(pad, VirtualControllerType.Xbox));
                var set = Set("LeftTrigger", Ramp("", "Button 5", A, "Button 1", B));
                set.Rows.Add(new MappingRow
                {
                    Target = "RightTrigger", LayerMask = "Base",
                    Sources = new List<MappingSource> { Ramp("", "Gamepad ButtonA", "", "Gamepad ButtonB", "") },
                });
                SettingsService.RefreshMappingSetsFromLegacy();
                var rows = SettingsManager.SlotMappingSets[Slot].Rows;
                var left = Assert.Single(rows.Single(r => r.Target == "LeftTrigger" && r.LayerMask == "Base").Sources);
                Assert.Equal(("Button 5", A), (left.ParamUp, left.ParamUpDeviceGuid));
                Assert.Equal("", left.ParamDown);
                var right = Assert.Single(rows.Single(r => r.Target == "RightTrigger" && r.LayerMask == "Base").Sources);
                Assert.Equal("Ramped", right.Kind);
            }
            finally { SettingsService.AfterMappingSetsRefreshed = savedHook; }
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
        }

        /// <summary>A key's controller is an edit like its input: the row is
        /// kept and the settings saved when it alone changes, a saved-before
        /// key taking the one its picker shows included.</summary>
        [Fact]
        public void AKeysControllerIsAnEditTheHooksSave()
        {
            string mw = RepoText("PadForge.App", "MainWindow.xaml.cs");
            foreach (string name in new[] { "ParamUpDeviceGuid", "ParamDownDeviceGuid", "ParamModifierDeviceGuid" })
                Assert.Equal(2, mw.Split("or nameof(MappingSourceItem." + name + ")").Length - 1);
            // The engine reads every key through the app once it starts.
            string svc = RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("PadForge.Engine.Common.Mapping.SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;", svc);
            Assert.Contains("PadForge.Engine.Common.Mapping.SourceCoercion.KeyHeldProvider = null;", svc);
        }

        /// <summary>Both controller previews draw a key's wire under the
        /// controller the key reads.</summary>
        [Fact]
        public void ThePreviewsDrawAKeyUnderTheControllerItReads()
        {
            foreach (var view in new[] { "ControllerModelView", "ControllerModel2DView" })
            {
                string src = RepoText("PadForge.App", "Views", view + ".Annotations.cs");
                Assert.Contains("AppendAnnotationParamWire(rows, src.ParamUp, src.ParamUpReadDevice, src.ParamUpInputChoice);", src);
                Assert.Contains("AppendAnnotationParamWire(rows, src.ParamDown, src.ParamDownReadDevice, src.ParamDownInputChoice);", src);
                Assert.Contains("AppendAnnotationWire(rows, readDevice, choice?.DeviceLabel, name);", src);
            }
            var item = new MappingSourceItem { Kind = "Incremental", DeviceGuid = A, ParamUp = "Button 5", ParamDown = "Button 1" };
            Assert.Equal(A, item.ParamUpReadDevice);
            item.ParamDownDeviceGuid = B;
            Assert.Equal(B, item.ParamDownReadDevice);
            item.ParamModifierDeviceGuid = "";
            Assert.Equal("", item.ParamModifierReadDevice);
        }

        // ── Consumption ────────────────────────────────────────────────

        /// <summary>A key is consumed on the controller it reads. A keyboard
        /// key picked as Up on a source that names a pad was never consumed,
        /// so it reached the game too.</summary>
        [Fact]
        public void AKeyIsConsumedOnTheControllerItReads()
        {
            var keyboard = Device(A, InputDeviceType.Keyboard);
            Device(B);
            Set("LeftThumbAxisX", Ramp(B, "Button 65", A, "Button 66", B));
            var collect = typeof(InputService).GetMethod("CollectSuppressedInputs", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(collect);
            var keys = new HashSet<int>();
            collect.Invoke(null, new object[] { keyboard, keys, new HashSet<int>() });
            Assert.Contains(65, keys);
            Assert.DoesNotContain(66, keys);
        }
    }
}
