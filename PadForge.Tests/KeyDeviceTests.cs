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
        /// read once a frame, so it reads the whole slot through the app. An
        /// "(Any Device)" Invert on Hold modifier on a row each pass reads
        /// apart is read on the pass device, by Step 3 and not through the
        /// app's key reader: the controller that holds it inverts its own
        /// pass and no other.</summary>
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
            var a = Device(A);
            var b = Device(B);
            var set = Set("LeftTrigger",
                new MappingSource { Kind = "Direct", DeviceGuid = "", Descriptor = "Button 0" },
                new MappingSource { Kind = "InvertOnHold", DeviceGuid = "", ParamModifier = "Button 1", ParamModifierDeviceGuid = "" });

            // A holds the input and the modifier: its own pass is inverted to
            // rest, and B's pass, at rest with no modifier held, stays there.
            // Read across the slot, the modifier inverted B's pass to a full
            // pull.
            a.InputState.Buttons[0] = true;
            a.InputState.Buttons[1] = true;
            Assert.Equal(0, Frame(set, a, b).LeftTrigger);
            // No modifier held: A's pass pulls as held.
            a.InputState.Buttons[1] = false;
            Assert.Equal(ushort.MaxValue, Frame(set, a, b).LeftTrigger);
            a.InputState.Buttons[0] = false;
            Assert.Equal(0, Frame(set, a, b).LeftTrigger);
            // The modifier on B alone inverts B's pass, at rest, to a full
            // pull.
            b.InputState.Buttons[1] = true;
            Assert.Equal(ushort.MaxValue, Frame(set, a, b).LeftTrigger);
            Assert.Empty(asked);
        }

        /// <summary>A modifier on a named controller inverts a stick row
        /// from that controller alone, as it does a trigger row
        /// (<see cref="TheModifierReadsTheControllerItNames"/>): the same
        /// button on the row's own controller inverts nothing.</summary>
        [Fact]
        public void AModifierOnANamedControllerInvertsAStickRow()
        {
            SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            var a = Device(A);
            var b = Device(B);
            var set = Set("LeftThumbAxisX",
                new MappingSource { Kind = "Direct", DeviceGuid = A, Descriptor = "Button 0" },
                new MappingSource
                {
                    Kind = "InvertOnHold", DeviceGuid = A,
                    ParamModifier = "Button 1", ParamModifierDeviceGuid = B,
                });
            a.InputState.Buttons[0] = true;
            a.InputState.Buttons[1] = true;
            Assert.True(Frame(set, a, b).ThumbLX > 30000);
            b.InputState.Buttons[1] = true;
            Assert.True(Frame(set, a, b).ThumbLX < -30000);
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

        /// <summary>The controller a kept input was picked on is saved with
        /// the source, only where it differs from the source's own device. A
        /// source that never left it saves as it did before, a file from an
        /// older build loads with none, and a serializer that does not know
        /// the attribute skips it, as an older build does. Copies carry
        /// it.</summary>
        [Fact]
        public void TheSettingsFileKeepsTheControllerAKeptInputWasPickedOn()
        {
            var ser = new XmlSerializer(typeof(MappingSource));
            string Xml(MappingSource source)
            {
                var writer = new StringWriter();
                ser.Serialize(writer, source);
                return writer.ToString();
            }

            var followed = Ramp(B, "Button 5", B, "Button 1", B);
            followed.Descriptor = "Button 3";
            followed.DescriptorDeviceGuid = A;
            string xml = Xml(followed);
            Assert.Contains("DescriptorDeviceGuid=\"" + A + "\"", xml);
            var back = (MappingSource)ser.Deserialize(new StringReader(xml));
            Assert.Equal((B, "Button 3", A), (back.DeviceGuid, back.Descriptor, back.DescriptorDeviceGuid));
            Assert.Equal(A, followed.Clone().DescriptorDeviceGuid);

            // An input picked under "(Any Device)" keeps that too.
            followed.DescriptorDeviceGuid = "";
            Assert.Equal("", ((MappingSource)ser.Deserialize(new StringReader(Xml(followed)))).DescriptorDeviceGuid);

            // No controller to remember: nothing written, and nothing read
            // from a file that has none.
            Assert.DoesNotContain("DescriptorDeviceGuid", Xml(Ramp(B, "Button 5", B, "Button 1", B)));
            var old = (MappingSource)ser.Deserialize(new StringReader(
                "<MappingSource Kind=\"Incremental\" DeviceGuid=\"" + B + "\" Descriptor=\"Button 3\" ParamUp=\"Button 5\" />"));
            Assert.Null(old.DescriptorDeviceGuid);

            // An attribute the reading build does not know is passed over.
            var newer = (MappingSource)ser.Deserialize(new StringReader(
                "<MappingSource Kind=\"Incremental\" DeviceGuid=\"" + B + "\" SavedByALaterBuild=\"" + A + "\" ParamUp=\"Button 5\" />"));
            Assert.Equal((B, "Button 5"), (newer.DeviceGuid, newer.ParamUp));

            // The clipboard's rows carry it, and a payload without it reads none.
            var clip = PadSetting.FromJson(new PadSetting
            {
                SlotMultiSourceRows = new List<MappingRow>
                {
                    new() { Target = "LeftTrigger", LayerMask = "Base", Sources = new List<MappingSource> { followed.Clone(), Ramp(B, "Button 5", B, "", null) } },
                },
            }.ToJson());
            Assert.Equal("", clip.SlotMultiSourceRows[0].Sources[0].DescriptorDeviceGuid);
            Assert.Null(clip.SlotMultiSourceRows[0].Sources[1].DescriptorDeviceGuid);
        }

        /// <summary>The controller of a kept input moves with the keys
        /// wherever a copy moves them, and an input whose controller has no
        /// counterpart goes with it, as a key does: left behind, a later
        /// switch back to Direct read it on the keys' controller. A source
        /// whose kind reads its input reads it on its own device, so a
        /// stray value there is dropped and the input stays.</summary>
        [Fact]
        public void RetargetingMovesTheControllerOfAKeptInputAndDropsTheInputWithIt()
        {
            MappingSource Followed(string kind = "Ramped")
            {
                var src = Ramp(B, "Button 5", B, "Button 1", B);
                src.Kind = kind;
                src.Descriptor = "Button 3";
                src.DescriptorDeviceGuid = A;
                return src;
            }

            var moved = Followed();
            Assert.False(moved.RetargetKeyDevices(g => g == A ? C : g));
            Assert.Equal((C, "Button 3", B), (moved.DescriptorDeviceGuid, moved.Descriptor, moved.ParamUpDeviceGuid));

            // No counterpart: the input goes, and the source keeps its keys.
            foreach (string kind in new[] { "Ramped", "Incremental", "InvertOnHold" })
            {
                var dropped = Followed(kind);
                dropped.RetargetKeyDevices(g => g == A ? null : g);
                Assert.Equal(((string)null, "", "Button 5"), (dropped.DescriptorDeviceGuid, dropped.Descriptor, dropped.ParamUp));
            }

            // A kind that reads its input reads it on its own device.
            foreach (Func<string, string> retarget in new Func<string, string>[] { g => g == A ? null : g, g => g == A ? C : g })
            {
                var direct = Followed("Direct");
                Assert.False(direct.RetargetKeyDevices(retarget));
                Assert.Equal(((string)null, "Button 3"), (direct.DescriptorDeviceGuid, direct.Descriptor));
            }

            // An input picked under any controller, or with none remembered, stays.
            var any = Followed();
            any.DescriptorDeviceGuid = "";
            any.RetargetKeyDevices(g => g == B ? C : null);
            Assert.Equal(("", "Button 3"), (any.DescriptorDeviceGuid, any.Descriptor));
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

        /// <summary>An "(Any Device)" stick ring key reads the controllers
        /// that have the stick. A keyboard's axes rest at zero, which a ring
        /// reads as a stick held in its corner, so a keyboard on the slot
        /// must not hold the key, whichever pass comes first.</summary>
        [Fact]
        public void AnAnyControllerRingKeyIgnoresADeviceWithoutTheStick()
        {
            SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            var keyboard = Device(B, InputDeviceType.Keyboard);
            Array.Clear(keyboard.InputState.Axis);
            var pad = Device(A);
            var set = Set("LeftThumbAxisX", Ramp("", SourceCoercion.LeftStickRingDescriptor, "", "", null));

            Assert.Equal(0, Frame(set, keyboard, pad).ThumbLX);   // the pad's stick at rest
            Assert.Equal(0, Frame(set, pad, keyboard).ThumbLX);
            Assert.False(InputManager.KeyHeld("", SourceCoercion.LeftStickRingDescriptor, Slot));

            pad.InputState.Axis[1] = 65535;                       // the pad's own stick at its rim
            Assert.True(InputManager.KeyHeld("", SourceCoercion.LeftStickRingDescriptor, Slot));
            Assert.True(Frame(set, keyboard, pad).ThumbLX > 30000);
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

        /// <summary>A row of one input, "Button 3" on controller A, and an
        /// Invert on Hold modifier that kept "Button 4" from an earlier kind
        /// and inverts the row on "Button 1". <paramref name="third"/> adds a
        /// second input, "Button 5", which sends the row through the
        /// builders that combine several sources.</summary>
        private static MappingSet ModifierRow(string target, bool modifierFirst, bool third, bool modifierInverted)
        {
            var input = new MappingSource { Kind = "Direct", DeviceGuid = A, Descriptor = "Button 3" };
            var modifier = new MappingSource
            {
                Kind = "InvertOnHold", DeviceGuid = A, Descriptor = "Button 4", Invert = modifierInverted,
                ParamModifier = "Button 1", ParamModifierDeviceGuid = A,
            };
            var sources = modifierFirst
                ? new List<MappingSource> { modifier, input }
                : new List<MappingSource> { input, modifier };
            if (third) sources.Add(new MappingSource { Kind = "Direct", DeviceGuid = A, Descriptor = "Button 5" });
            return Set(target, sources.ToArray());
        }

        /// <summary>The modifier first or second in its row, alone with the
        /// row's input or with a second one, its own Invert off and on.</summary>
        public static TheoryData<bool, bool, bool> ModifierRows()
        {
            var rows = new TheoryData<bool, bool, bool>();
            foreach (bool modifierFirst in new[] { false, true })
                foreach (bool third in new[] { false, true })
                    foreach (bool inverted in new[] { false, true })
                        rows.Add(modifierFirst, third, inverted);
            return rows;
        }

        /// <summary>An Invert on Hold source is a row modifier and no source
        /// of its row: Step 3 skips it and reads its modifier key alone. So
        /// the input it kept from an earlier kind pulls no trigger, presses
        /// no button and pushes no stick, held or not, with the source's own
        /// Invert off or on, while the row's own input still does all three
        /// and the modifier key still inverts the trigger and the stick. The
        /// gamepad's row loop and the per-target evaluators the other
        /// controller types read through agree, wherever the modifier sits
        /// in the row and whether the row has one input or two. With its
        /// Invert on, a modifier next to the row's first input has the shape
        /// of a pair's negative leg (the same controller, the opposite
        /// Invert) and is no leg of one. The source evaluator has no case for
        /// a modifier, and no row hands it one.</summary>
        [Theory]
        [MemberData(nameof(ModifierRows))]
        public void AModifiersKeptInputMovesNothing(bool modifierFirst, bool third, bool modifierInverted)
        {
            SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            var a = Device(A);
            void Hold(params int[] buttons)
            {
                Release(a);
                foreach (int button in buttons) a.InputState.Buttons[button] = true;
            }
            const int Own = 3, Kept = 4, ModifierKey = 1;

            var trigger = ModifierRow("LeftTrigger", modifierFirst, third, modifierInverted);
            short pull;
            foreach (bool keptHeld in new[] { false, true })
            {
                if (keptHeld) Hold(Kept); else Hold();
                Assert.Equal(0, Frame(trigger, a).LeftTrigger);
                Assert.True(InputManager.TryEvaluateMappingSetRawTrigger(a.InputState, trigger, A, Slot, "LeftTrigger", out pull));
                Assert.Equal(short.MinValue, pull);
            }
            Hold(Own);
            Assert.Equal(ushort.MaxValue, Frame(trigger, a).LeftTrigger);
            Assert.True(InputManager.TryEvaluateMappingSetRawTrigger(a.InputState, trigger, A, Slot, "LeftTrigger", out pull));
            Assert.Equal(short.MaxValue, pull);
            Hold(Kept, ModifierKey);                        // the row at rest, inverted
            Assert.Equal(ushort.MaxValue, Frame(trigger, a).LeftTrigger);
            Assert.True(InputManager.TryEvaluateMappingSetRawTrigger(a.InputState, trigger, A, Slot, "LeftTrigger", out pull));
            Assert.Equal(short.MaxValue, pull);

            var button = ModifierRow("ButtonA", modifierFirst, third, modifierInverted);
            bool pressed;
            foreach (bool keptHeld in new[] { false, true })
            {
                if (keptHeld) Hold(Kept); else Hold();
                Assert.False(Frame(button, a).IsButtonPressed(Gamepad.A));
                Assert.True(InputManager.TryEvaluateMappingSetButton(a.InputState, button, A, Slot, "ButtonA", 50, out pressed));
                Assert.False(pressed);
            }
            Hold(Own);
            Assert.True(Frame(button, a).IsButtonPressed(Gamepad.A));
            Assert.True(InputManager.TryEvaluateMappingSetButton(a.InputState, button, A, Slot, "ButtonA", 50, out pressed));
            Assert.True(pressed);

            var stick = ModifierRow("LeftThumbAxisX", modifierFirst, third, modifierInverted);
            short axis;
            foreach (bool keptHeld in new[] { false, true })
            {
                if (keptHeld) Hold(Kept); else Hold();
                Assert.Equal(0, Frame(stick, a).ThumbLX);
                Assert.True(InputManager.TryEvaluateMappingSetBipolarAxis(a.InputState, stick, A, Slot, "LeftThumbAxisX", out axis));
                Assert.Equal(0, axis);
            }
            Hold(Own);
            Assert.True(Frame(stick, a).ThumbLX > 30000);
            Assert.True(InputManager.TryEvaluateMappingSetBipolarAxis(a.InputState, stick, A, Slot, "LeftThumbAxisX", out axis));
            Assert.True(axis > 30000);
            Hold(Own, Kept, ModifierKey);                   // the row's input, inverted
            Assert.True(Frame(stick, a).ThumbLX < -30000);
            Assert.True(InputManager.TryEvaluateMappingSetBipolarAxis(a.InputState, stick, A, Slot, "LeftThumbAxisX", out axis));
            Assert.True(axis < -30000);
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

        /// <summary>A pair of key sources on a stick row, the second inverted
        /// so it pushes the other way, on a slot of two VR controllers. The
        /// pair is read on the pass in hand as a lone key source is. It took
        /// the walk over the controllers that answer "(Any Device)", found
        /// none, and read the stick at rest whatever was held.</summary>
        [Fact]
        public void APairOfKeySourcesIsReadOnASlotNoDeviceAnswersFor()
        {
            SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            var left = Device(A, InputDeviceType.VrController);
            var right = Device(B, InputDeviceType.VrController);
            var back = Ramp("", "Button 6", A, "Button 2", B);
            back.Invert = true;
            var set = Set("LeftThumbAxisX", Ramp("", "Button 5", A, "Button 1", B), back);

            left.InputState.Buttons[5] = true;
            Assert.True(Frame(set, left, right).ThumbLX > 30000);
            Release(left, right);
            Frame(set, left, right);
            left.InputState.Buttons[6] = true;
            Assert.True(Frame(set, left, right).ThumbLX < -30000);
        }

        /// <summary>Control: the same pair with a gamepad on the slot reads
        /// both ways through the walk.</summary>
        [Fact]
        public void APairOfKeySourcesBesideAGamepadReadsBothWays_Control()
        {
            SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            var left = Device(A, InputDeviceType.VrController);
            var right = Device(B, InputDeviceType.VrController);
            var pad = Device(C);
            var back = Ramp("", "Button 6", A, "Button 2", B);
            back.Invert = true;
            var set = Set("LeftThumbAxisX", Ramp("", "Button 5", A, "Button 1", B), back);

            left.InputState.Buttons[5] = true;
            Assert.True(Frame(set, pad, left, right).ThumbLX > 30000);
            Release(left, right);
            Frame(set, pad, left, right);
            left.InputState.Buttons[6] = true;
            Assert.True(Frame(set, pad, left, right).ThumbLX < -30000);
        }

        /// <summary>An "(Any Device)" key source gated on a stick ring reads
        /// its gate on a controller that has the stick, never on a keyboard,
        /// whose zeroed axes read as the ring pushed out. A row read once a
        /// frame took the key source on the pass in hand, the keyboard's when
        /// its pass came first, and stepped the value with the stick at rest.
        /// The pad's own ring pushed out is the control.</summary>
        [Theory]
        [InlineData("LeftTrigger", false)]
        [InlineData("LeftTrigger", true)]
        [InlineData("LeftThumbAxisX", false)]
        [InlineData("ButtonA", false)]
        public void AGatedKeySourceReadsItsGateOnlyWhereTheGateCanBeRead(string target, bool stickTrim)
        {
            SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            var keyboard = Device(B, InputDeviceType.Keyboard);
            Array.Clear(keyboard.InputState.Axis);
            var pad = Device(A);
            var gated = new MappingSource
            {
                Kind = "Incremental", DeviceGuid = "", ParamUp = "Button 0", ParamUpDeviceGuid = A,
                ParamRate = 1, ParamMin = 0, ParamMax = 1, ParamSticky = true,
                GateDescriptor = SourceCoercion.RightStickRingDescriptor,
            };
            var set = Set(target, gated, new MappingSource { Kind = "Direct", DeviceGuid = "", Descriptor = "Button 1" });
            if (stickTrim) set.Rows[0].CombineMode = "StickTrim";
            bool Moved(Gamepad gp) => target switch
            {
                "LeftTrigger" => gp.LeftTrigger > 0,
                "LeftThumbAxisX" => gp.ThumbLX != 0,
                _ => gp.IsButtonPressed(Gamepad.A),
            };

            pad.InputState.Buttons[0] = true;             // Up held, the right stick at rest
            for (int i = 0; i < 4; i++)
                Assert.False(Moved(TimedFrame(set, 0.25, keyboard, pad)), $"frame {i}: the keyboard's zeroed axes opened the gate");

            pad.InputState.Axis[3] = 65535;               // the pad's own ring pushed out
            var last = default(Gamepad);
            for (int i = 0; i < 3; i++) last = TimedFrame(set, 0.25, keyboard, pad);
            Assert.True(Moved(last), "control: the pad's ring opens the gate");
        }

        /// <summary>On a slot no controller answers "(Any Device)" for, a
        /// gated key source stands in only on a pass that has every axis its
        /// gate reads, the rule a phone's menu cell keeps. A VR controller
        /// holds 0 where it has no stick of the gamepad's, which a stick ring
        /// gate read as pushed out.</summary>
        [Fact]
        public void AGatedKeySourceStandsInOnlyOnAPassThatHasItsGatesAxes()
        {
            SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            var left = Device(A, InputDeviceType.VrController);
            var right = Device(B, InputDeviceType.VrController);
            Array.Clear(left.InputState.Axis);
            Array.Clear(right.InputState.Axis);
            var gated = new MappingSource
            {
                Kind = "Incremental", DeviceGuid = "", ParamUp = "Button 5", ParamUpDeviceGuid = A,
                ParamDown = "Button 1", ParamDownDeviceGuid = B,
                ParamRate = 1, ParamMin = 0, ParamMax = 1, ParamSticky = true,
                GateDescriptor = SourceCoercion.RightStickRingDescriptor,
            };
            var set = Set("LeftTrigger", gated);
            left.InputState.Buttons[5] = true;
            for (int i = 0; i < 3; i++)
                Assert.Equal(0, TimedFrame(set, 0.25, left, right).LeftTrigger);

            // Control: the same source with no gate stands in and steps.
            gated.GateDescriptor = "";
            Assert.True(TimedFrame(set, 0.25, left, right).LeftTrigger > 0);
        }

        /// <summary>False-positive guard for the walk a gated key source now
        /// takes: a gate that reads no axis reads on every controller that
        /// answers "(Any Device)", a keyboard's own button included, as a
        /// Direct source's gate does, whichever pass comes first.</summary>
        [Fact]
        public void AGatedKeySourceStillReadsAButtonGateOnAKeyboard()
        {
            SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            var keyboard = Device(B, InputDeviceType.Keyboard);
            Array.Clear(keyboard.InputState.Axis);
            var pad = Device(A);
            var gated = new MappingSource
            {
                Kind = "Incremental", DeviceGuid = "", ParamUp = "Button 0", ParamUpDeviceGuid = A,
                ParamRate = 1, ParamMin = 0, ParamMax = 1, ParamSticky = true,
                GateDescriptor = "Button 3",
            };
            var set = Set("LeftTrigger", gated, new MappingSource { Kind = "Direct", DeviceGuid = "", Descriptor = "Button 1" });
            pad.InputState.Buttons[0] = true;
            Assert.Equal(0, TimedFrame(set, 0.25, pad, keyboard).LeftTrigger);  // control: no gate held

            keyboard.InputState.Buttons[3] = true;          // the gate held on the keyboard
            Assert.True(TimedFrame(set, 0.25, pad, keyboard).LeftTrigger > 0, "the pad's pass first");
            Assert.True(TimedFrame(set, 0.25, keyboard, pad).LeftTrigger > 0, "the keyboard's pass first");
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
        /// its picker lists built the way the app builds them. With
        /// <paramref name="xboxRecorded"/> false the slot still names the
        /// Xbox controller but the device list has no record of it, so the
        /// lists carry none of its inputs.</summary>
        private static TwoControllers Populate(bool xboxRecorded = true)
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
            if (xboxRecorded) SettingsManager.UserDevices.Items.Add(xbox);
            SettingsManager.UserDevices.Items.Add(wii);
            var vm = new MainViewModel();
            var svc = new InputService(vm) { SettingsService = new SettingsService(vm) };
            var pad = vm.Pads[0];
            pad.OutputType = VirtualControllerType.PlayStation;
            pad.MappedDevices.Add(new PadViewModel.MappedDeviceInfo { InstanceGuid = xbox.InstanceGuid, Name = "Xbox Controller", IsOnline = true });
            pad.MappedDevices.Add(new PadViewModel.MappedDeviceInfo { InstanceGuid = wii.InstanceGuid, Name = "Nintendo Wii Remote", IsOnline = true });
            var p = new TwoControllers { Pad = pad, Xbox = xbox, Wii = wii, Service = svc };
            Rebuild(p);
            return p;
        }

        /// <summary>The slot's picker lists built again, as a device change
        /// rebuilds them.</summary>
        private static void Rebuild(TwoControllers p)
            => typeof(InputService).GetMethod("PopulateAvailableInputs", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(p.Service, new object[] { p.Pad, null });

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
            // list, carries its controller the same way, and the source goes
            // to that controller under the name the picker gives it
            // (AnInvertOnHoldSourceFollowsItsModifier).
            var ioh = new MappingSourceItem { Kind = "InvertOnHold" };
            row.ExtraSources.Add(ioh);
            var three = row.ParamInputs.First(c => c.Descriptor == "Button 3" && c.DeviceGuid == B);
            ioh.ParamModifierInputChoice = three;
            Assert.Equal(B, ioh.ParamModifierDeviceGuid);
            Assert.Same(three, ioh.ParamModifierInputChoice);
            Assert.Equal((B, three.DeviceLabel), (ioh.DeviceGuid, ioh.DeviceLabel));
            Assert.Equal((B, B), (ioh.ToDomain().ParamModifierDeviceGuid, ioh.ToDomain().DeviceGuid));
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

        /// <summary>A key saved before keys carried a controller takes one
        /// only from a list that carries its source's own controller. Built
        /// while that controller has no record, the list offered the Wii
        /// Remote's Button 3, the adoption stored it, and the Xbox
        /// controller's Button 3 no longer raised the value. The picker shows
        /// no selection meanwhile, and the list built with the controller's
        /// inputs settles it.</summary>
        [Fact]
        public void ALegacyKeyWaitsForAListThatCarriesItsSourcesController()
        {
            var p = Populate(xboxRecorded: false);
            var row = p.Row("LeftTrigger");
            Assert.DoesNotContain(row.ParamInputs, c => c.DeviceGuid == A);
            Assert.Contains(row.ParamInputs, c => c.Descriptor == "Button 3" && c.DeviceGuid == B);

            var extra = MappingSourceItem.FromDomain(new MappingSource { Kind = "Incremental", DeviceGuid = A, ParamUp = "Button 3" });
            row.ExtraSources.Add(extra);
            Assert.Null(extra.ParamUpDeviceGuid);
            Assert.Equal(A, extra.DeviceGuid);
            Assert.Null(extra.ParamUpInputChoice);

            SettingsManager.UserDevices.Items.Add(p.Xbox);
            Rebuild(p);
            Assert.Equal(A, extra.ParamUpDeviceGuid);
            Assert.Equal(A, extra.DeviceGuid);
        }

        /// <summary>A row a load fills takes no controller for an old key
        /// from the list in hand, which the slot rebuilds after its rows load
        /// and which can still be the previous controllers'. The rebuild
        /// decides.</summary>
        [Fact]
        public void ALoadedRowsOldKeysWaitForTheListBuiltAfterIt()
        {
            var p = Populate();
            var row = p.Row("LeftTrigger");
            var extra = MappingSourceItem.FromDomain(new MappingSource { Kind = "Incremental", DeviceGuid = "", ParamUp = "Button 3" });
            row.BeginLoadRow();
            row.ExtraSources.Add(extra);
            row.EndLoadRow();
            Assert.Null(extra.ParamUpDeviceGuid);

            Rebuild(p);
            Assert.NotNull(extra.ParamUpDeviceGuid);
        }

        /// <summary>An extra source keeps the controller its input was picked
        /// on through a trip to a key kind. With Up and Down picked on two
        /// controllers it names none, and back on Direct its kept input read
        /// on every controller on the slot.</summary>
        [Fact]
        public void AnExtraSwitchedBackToDirectReadsItsInputOnTheControllerItWasPickedFrom()
        {
            var p = Populate();
            var row = p.Row("LeftTrigger");
            var extra = new MappingSourceItem();
            row.ExtraSources.Add(extra);
            var picked = row.AvailableInputs.First(c => c.Descriptor == "Button 3" && c.DeviceGuid == A);
            extra.SelectedInput = picked;
            Assert.Equal(A, extra.DeviceGuid);
            string label = extra.DeviceLabel;
            Assert.False(string.IsNullOrEmpty(label));

            extra.Kind = "Incremental";
            extra.ParamUpInputChoice = Key(row, "Button 1", B);
            extra.ParamDownInputChoice = Key(row, "Button 2", A);
            Assert.Equal("", extra.DeviceGuid);

            extra.Kind = "Direct";
            Assert.Equal(A, extra.DeviceGuid);
            var saved = extra.ToDomain();
            Assert.Equal((A, "Button 3"), (saved.DeviceGuid, saved.Descriptor));
            // The picker and the name under it show that controller again.
            Assert.Same(picked, extra.SelectedInput);
            Assert.Equal(label, extra.DeviceLabel);

            // A fresh pick names its own controller, and the next trip keeps it.
            extra.SelectedInput = row.AvailableInputs.First(c => c.Descriptor == "Button 4" && c.DeviceGuid == B);
            extra.Kind = "Ramped";
            extra.ParamUpInputChoice = Key(row, "Button 5", A);
            Assert.Equal(A, extra.DeviceGuid);
            extra.Kind = "Direct";
            Assert.Equal(B, extra.DeviceGuid);

            // A controller the row's list does not carry gives no name to
            // resolve, so the name it showed comes back with it.
            var unlisted = new MappingSourceItem { DeviceGuid = C, DeviceLabel = "Pad C", Descriptor = "Button 3" };
            unlisted.Kind = "Incremental";
            unlisted.SetParamKey(MappingSourceItem.ParamRecordTarget.Up, "Button 1", B);
            unlisted.SetParamKey(MappingSourceItem.ParamRecordTarget.Down, "Button 2", A);
            Assert.Equal("", unlisted.DeviceGuid);
            Assert.NotEqual("Pad C", unlisted.DeviceLabel);
            unlisted.Kind = "Direct";
            Assert.Equal((C, "Pad C"), (unlisted.DeviceGuid, unlisted.DeviceLabel));
        }

        /// <summary>The controller an extra's kept input was picked on
        /// outlasts a save and a load in a key kind. It was remembered in the
        /// view model alone, so once the settings were loaded again the
        /// switch back to Direct left the source on the controller its keys
        /// share and read the kept input there, a button nobody picked on
        /// that controller. It is saved with the source, and here it goes
        /// through the settings file's serializer and the grid's loader.</summary>
        [Theory]
        [InlineData("Incremental")]
        [InlineData("Ramped")]
        public void AnExtraSwitchedBackToDirectAfterAReloadReadsItsInputWhereItWasPicked(string keyKind)
        {
            var p = Populate();
            var row = p.Row("LeftTrigger");
            var extra = new MappingSourceItem();
            row.ExtraSources.Add(extra);
            extra.SelectedInput = row.AvailableInputs.First(c => c.Descriptor == "Button 3" && c.DeviceGuid == A);
            string padLabel = extra.DeviceLabel;

            extra.Kind = keyKind;
            extra.ParamUpInputChoice = Key(row, "Button 1", B);
            Assert.Equal(B, extra.DeviceGuid);
            var saved = extra.ToDomain();
            Assert.Equal((keyKind, B, "Button 3", A), (saved.Kind, saved.DeviceGuid, saved.Descriptor, saved.DescriptorDeviceGuid));

            var ser = new XmlSerializer(typeof(MappingSource));
            var writer = new StringWriter();
            ser.Serialize(writer, saved);
            var fromFile = (MappingSource)ser.Deserialize(new StringReader(writer.ToString()));
            SettingsManager.SlotMappingSets[p.Pad.PadIndex] = new MappingSet
            {
                Rows =
                {
                    new MappingRow
                    {
                        Target = "LeftTrigger", LayerMask = "Base",
                        Sources = new List<MappingSource>
                        {
                            new() { Kind = "Direct", DeviceGuid = A, Descriptor = "Axis 2" },
                            fromFile,
                        },
                    },
                },
            };
            InputService.RefreshMappingsToViewModel(p.Pad);
            Rebuild(p);
            var loaded = Assert.Single(p.Row("LeftTrigger").ExtraSources);
            Assert.Equal((keyKind, B, "Button 3"), (loaded.Kind, loaded.DeviceGuid, loaded.Descriptor));
            // A save with nothing edited keeps it.
            Assert.Equal(A, loaded.ToDomain().DescriptorDeviceGuid);

            loaded.Kind = "Direct";
            Assert.Equal((A, padLabel), (loaded.DeviceGuid, loaded.DeviceLabel));
            var direct = loaded.ToDomain();
            Assert.Equal((A, "Button 3", (string)null), (direct.DeviceGuid, direct.Descriptor, direct.DescriptorDeviceGuid));
        }

        /// <summary>A controller the row's list does not carry gives the row
        /// no name to resolve, so the loader names the kept input's
        /// controller as it names the source's own: by its device record,
        /// or by the start of its id where it has none.</summary>
        [Fact]
        public void TheLoaderNamesTheControllerOfAKeptInputTheListDoesNotCarry()
        {
            var p = Populate();
            var kept = Ramp(B, "Button 1", B, "Button 2", B);
            kept.Descriptor = "Button 3";
            kept.DescriptorDeviceGuid = C;
            SettingsManager.SlotMappingSets[p.Pad.PadIndex] = new MappingSet
            {
                Rows =
                {
                    new MappingRow
                    {
                        Target = "LeftTrigger", LayerMask = "Base",
                        Sources = new List<MappingSource> { new() { Kind = "Direct", DeviceGuid = A, Descriptor = "Axis 2" }, kept },
                    },
                },
            };
            InputService.RefreshMappingsToViewModel(p.Pad);
            Rebuild(p);
            var loaded = Assert.Single(p.Row("LeftTrigger").ExtraSources);
            Assert.Equal(B, loaded.DeviceGuid);

            loaded.Kind = "Direct";
            Assert.Equal((C, C.Substring(0, 8) + "…"), (loaded.DeviceGuid, loaded.DeviceLabel));
        }

        /// <summary>The controller of a kept input is saved only while the
        /// source sits on another one: never in Direct, where the input
        /// reads the source's own device, and not while the keys share the
        /// controller the input was picked on. It stays remembered across
        /// those states, and an input picked under "(Any Device)" keeps that
        /// too.</summary>
        [Fact]
        public void TheControllerOfAKeptInputIsSavedOnlyWhereItDiffersFromTheSourcesOwn()
        {
            var p = Populate();
            var row = p.Row("LeftTrigger");
            var extra = new MappingSourceItem();
            row.ExtraSources.Add(extra);
            extra.SelectedInput = row.AvailableInputs.First(c => c.Descriptor == "Button 3" && c.DeviceGuid == A);
            (string Device, string Picked) Saved()
            {
                var s = extra.ToDomain();
                return (s.DeviceGuid, s.DescriptorDeviceGuid);
            }
            Assert.Equal((A, (string)null), Saved());

            extra.Kind = "Incremental";
            Assert.Equal((A, (string)null), Saved());             // no key yet
            extra.ParamUpInputChoice = Key(row, "Button 5", A);
            Assert.Equal((A, (string)null), Saved());             // its key on the same controller
            extra.ParamDownInputChoice = Key(row, "Button 1", B);
            Assert.Equal(("", A), Saved());                       // keys on two controllers
            extra.ParamUpInputChoice = Key(row, "Button 2", B);
            Assert.Equal((B, A), Saved());

            // The keys come back to the input's controller, and leave again.
            extra.ParamUpInputChoice = Key(row, "Button 5", A);
            extra.ParamDownInputChoice = Key(row, "Button 6", A);
            Assert.Equal((A, (string)null), Saved());
            extra.ParamDownInputChoice = Key(row, "Button 1", B);
            Assert.Equal(("", A), Saved());

            extra.Kind = "Direct";
            Assert.Equal((A, (string)null), Saved());

            // An input picked under "(Any Device)" goes back there.
            var any = new MappingSourceItem
            {
                DeviceGuid = "", DeviceLabel = PadForge.Resources.Strings.Strings.Instance.Mapping_AnyDevice, Descriptor = "Gamepad ButtonA",
            };
            row.ExtraSources.Add(any);
            any.Kind = "Ramped";
            any.SetParamKey(MappingSourceItem.ParamRecordTarget.Up, "Button 1", B);
            var anySaved = any.ToDomain();
            Assert.Equal((B, ""), (anySaved.DeviceGuid, anySaved.DescriptorDeviceGuid));
            var reloaded = MappingSourceItem.FromDomain(anySaved);
            Assert.Equal(B, reloaded.DeviceGuid);
            reloaded.Kind = "Direct";
            Assert.Equal("", reloaded.DeviceGuid);

            // A saved controller on a source whose kind reads its input is
            // no memory: the input reads the source's own device.
            var strayDirect = MappingSourceItem.FromDomain(new MappingSource
            {
                Kind = "Direct", DeviceGuid = B, Descriptor = "Button 3", DescriptorDeviceGuid = A,
            });
            Assert.Null(strayDirect.ToDomain().DescriptorDeviceGuid);
            strayDirect.Kind = "Toggle";
            Assert.Equal(B, strayDirect.DeviceGuid);

            // An input that replaces the kept one under a key kind was not
            // picked on the remembered controller, and stays where it is.
            var replaced = MappingSourceItem.FromDomain(FollowedAway());
            Assert.Equal(A, replaced.ToDomain().DescriptorDeviceGuid);
            replaced.Descriptor = "Button 9";
            Assert.Null(replaced.ToDomain().DescriptorDeviceGuid);
            replaced.Kind = "Direct";
            Assert.Equal((B, "Button 9"), (replaced.DeviceGuid, replaced.Descriptor));
        }

        /// <summary>The saved controller of a kept input is a saved field
        /// like the rest: the source raises it with each change, where it
        /// follows its keys away, where it returns, and where another input
        /// takes the kept one's place, so the hook that marks the settings
        /// file dirty hears it (the list
        /// EverySourceFieldThatSavesMarksTheFileDirty checks).</summary>
        [Fact]
        public void TheSourceRaisesTheSavedControllerOfItsKeptInput()
        {
            var extra = new MappingSourceItem { DeviceGuid = A, Descriptor = "Button 3" };
            var raised = new List<string>();
            extra.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MappingSourceItem.DescriptorDeviceGuid)) raised.Add(extra.DescriptorDeviceGuid);
            };

            extra.Kind = "Incremental";
            extra.SetParamKey(MappingSourceItem.ParamRecordTarget.Up, "Button 1", B);
            Assert.Equal(new[] { A }, raised);

            extra.Descriptor = "Button 9";
            Assert.Equal(new[] { A, null }, raised);
            extra.Descriptor = "Button 3";
            Assert.Equal(new[] { A, null, A }, raised);

            extra.Kind = "Direct";
            Assert.Equal(new[] { A, null, A, null }, raised);
            Assert.Equal(A, extra.DeviceGuid);
        }

        /// <summary>Incremental and Ramp offer no Invert, so a source that
        /// comes to one of them in the editor from a kind that is neither
        /// leaves its Invert behind. Carried in, it flipped the keys' output
        /// with nothing on the row to show why: the engine applies a key
        /// source's Invert (SourceEvaluator), which rests an Incremental
        /// trigger at full pull. Invert on Hold is no key kind and changes
        /// nothing.</summary>
        [Theory]
        [InlineData("Incremental")]
        [InlineData("Ramped")]
        public void ASourceThatTakesAKeyKindLeavesItsInvertBehind(string kind)
        {
            var extra = new MappingSourceItem { DeviceGuid = A, Descriptor = "Button 3", Invert = true };
            extra.Kind = "InvertOnHold";
            Assert.True(extra.Invert);

            extra.Kind = kind;
            Assert.False(extra.Invert);
            Assert.False(extra.ToDomain().Invert);
            Assert.False(extra.IsKeyInvertShown);

            // Back on Direct it is off, and the checkbox there sets it again.
            extra.Kind = "Direct";
            Assert.False(extra.Invert);
            extra.Invert = true;
            Assert.False(extra.IsKeyInvertShown);

            // And straight from Direct, the same.
            extra.Kind = kind;
            Assert.False(extra.Invert);
        }

        /// <summary>A switch between the two key kinds keeps Invert. The
        /// only source that has it there was saved with it, and its checkbox
        /// is showing and checked at that moment. The engine applies the
        /// flag to an Incremental source and to a Ramp source on an axis, so
        /// dropped, an axis row turned around on a change of kind with a
        /// checked box in front of the user. The primary's kind holder keeps
        /// it the same way, and a trip through a kind that is no key kind
        /// still drops it on the way back in.</summary>
        [Theory]
        [InlineData("Incremental", "Ramped")]
        [InlineData("Ramped", "Incremental")]
        public void ASwitchBetweenTheTwoKeyKindsKeepsASavedInvert(string kind, string other)
        {
            var saved = new MappingSource { Kind = other, DeviceGuid = B, ParamUp = "Button 5", ParamUpDeviceGuid = B, Invert = true };
            var extra = MappingSourceItem.FromDomain(saved);
            Assert.True(extra.IsKeyInvertShown);
            var raised = new List<string>();
            extra.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            extra.Kind = kind;
            Assert.True(extra.Invert);
            Assert.True(extra.ToDomain().Invert);
            Assert.True(extra.IsKeyInvertShown);
            Assert.DoesNotContain(nameof(MappingSourceItem.Invert), raised);

            var p = Populate();
            var row = p.Row("LeftTrigger");
            row.LoadPrimaryKind(saved);
            row.PrimaryKindSource.Kind = kind;
            Assert.True(row.PrimaryKindSource.Invert);
            Assert.True(row.PrimaryKindSource.IsKeyInvertShown);
            Assert.True(row.PrimaryKindSource.ToDomain().Invert);

            // Through a kind that is no key kind, it goes on the way back in.
            extra.Kind = "Direct";
            Assert.True(extra.Invert);
            extra.Kind = other;
            Assert.False(extra.Invert);
            Assert.False(extra.IsKeyInvertShown);
        }

        /// <summary>A key source saved with Invert on keeps it through both
        /// loaders: the engine has applied it to that row all along, and
        /// dropping it would turn the row around. Hidden, it had no control
        /// to turn it off, so the row shows a checkbox for it while it is
        /// on, and unchecking it removes the checkbox with the flag.</summary>
        [Theory]
        [InlineData("Incremental")]
        [InlineData("Ramped")]
        public void AKeySourceSavedWithInvertKeepsItAndShowsACheckboxForIt(string kind)
        {
            var saved = new MappingSource { Kind = kind, DeviceGuid = B, ParamUp = "Button 5", ParamUpDeviceGuid = B, Invert = true };
            var extra = MappingSourceItem.FromDomain(saved);
            Assert.True(extra.Invert);
            Assert.True(extra.ToDomain().Invert);
            Assert.True(extra.IsKeyInvertShown);

            var raised = new List<string>();
            extra.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            extra.Invert = false;
            Assert.Contains(nameof(MappingSourceItem.IsKeyInvertShown), raised);
            Assert.False(extra.IsKeyInvertShown);
            Assert.False(extra.ToDomain().Invert);

            // Leaving the key kind takes the checkbox away with the flag
            // still on, which the kind that reads the Descriptor shows.
            var leaving = MappingSourceItem.FromDomain(saved);
            raised.Clear();
            leaving.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            leaving.Kind = "Direct";
            Assert.Contains(nameof(MappingSourceItem.IsKeyInvertShown), raised);
            Assert.True(leaving.Invert);
            Assert.False(leaving.IsKeyInvertShown);

            // The row's primary keeps it the same way and shows the same
            // checkbox, which turns it off there too. A source saved first
            // in its row with no primary input loads as the primary, and
            // with the checkbox on extra sources alone that row had no
            // control for the flag.
            var p = Populate();
            var row = p.Row("LeftTrigger");
            row.LoadPrimaryKind(saved);
            Assert.True(row.PrimaryKindSource.Invert);
            Assert.True(row.PrimaryKindSource.ToDomain().Invert);
            Assert.True(row.PrimaryKindSource.IsKeyInvertShown);
            row.PrimaryKindSource.Invert = false;
            Assert.False(row.PrimaryKindSource.IsKeyInvertShown);
            Assert.False(row.PrimaryKindSource.ToDomain().Invert);

            // The checkbox is the source's own Invert, shown by that flag
            // alone: once in the primary's strip, whose DataContext is the
            // row's PrimaryKindSource, and once in the row of an extra
            // source, and nowhere else. The strip's first row, beside the
            // Mode dropdown, runs up to the Ramp settings, the strip's
            // second child, so a checkbox before those is inside the strip.
            string xaml = RepoText("PadForge.App", "Views", "PadPage.xaml");
            const string shownBy = "Visibility=\"{Binding IsKeyInvertShown, Converter={StaticResource BoolToVisibilityConverter}}\"";
            int strip = xaml.IndexOf("<StackPanel DataContext=\"{Binding PrimaryKindSource}\"", StringComparison.Ordinal);
            int stripEnd = strip < 0 ? -1 : xaml.IndexOf(
                "Visibility=\"{Binding IsRampedKind, Converter={StaticResource BoolToVisibilityConverter}}\"", strip, StringComparison.Ordinal);
            int extras = xaml.IndexOf("<ItemsControl ItemsSource=\"{Binding ExtraSources}\">", StringComparison.Ordinal);
            Assert.True(strip > 0 && stripEnd > strip && extras > stripEnd, "the primary's strip or the extra sources' list was not found");
            int inPrimary = xaml.IndexOf(shownBy, strip, StringComparison.Ordinal);
            Assert.True(inPrimary > 0 && inPrimary < stripEnd, "no checkbox for a primary key source's saved Invert");
            int inExtra = xaml.IndexOf(shownBy, stripEnd, StringComparison.Ordinal);
            Assert.True(inExtra > extras, "no checkbox for an extra key source's saved Invert");
            Assert.Equal(-1, xaml.IndexOf(shownBy, inExtra + 1, StringComparison.Ordinal));
            Assert.Equal(inPrimary, xaml.IndexOf(shownBy, StringComparison.Ordinal));
            foreach (int shown in new[] { inPrimary, inExtra })
            {
                int open = xaml.LastIndexOf("<CheckBox", shown, StringComparison.Ordinal);
                string box = xaml[open..xaml.IndexOf("/>", shown, StringComparison.Ordinal)];
                Assert.Contains("Content=\"{Binding Pad_Invert, Source={x:Static strings:Strings.Instance}}\"", box);
                Assert.Contains("IsChecked=\"{Binding Invert, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", box);
                Assert.DoesNotContain("IsEnabled", box);
            }
        }

        /// <summary>Puts a controller of <see cref="Populate"/> on the slot
        /// Step 3 runs, as <see cref="Device"/> does for one it makes.</summary>
        private static void OnSlot(UserDevice ud)
        {
            var setting = new UserSetting { InstanceGuid = ud.InstanceGuid, ProductGuid = ud.ProductGuid, MapTo = Slot };
            setting.SetPadSetting(new PadSetting());
            SettingsManager.UserSettings.Items.Add(setting);
        }

        /// <summary>An extra source that returns to a key kind follows its
        /// keys again. Back on Direct it reads its input on the controller
        /// that input was picked on, and set to Incremental or Ramp once more
        /// with no key touched it stayed there, away from the controller its
        /// keys read. With the input's controller off, the key no longer
        /// moved the row, and unassigning that controller deleted the source
        /// with its own.</summary>
        [Theory]
        [InlineData("Incremental")]
        [InlineData("Ramped")]
        public void AnExtraReturnedToAKeyKindFollowsItsKeysAgain(string keyKind)
        {
            var p = Populate();
            var row = p.Row("LeftThumbAxisX");
            var extra = new MappingSourceItem();
            row.ExtraSources.Add(extra);
            extra.SelectedInput = row.AvailableInputs.First(c => c.Descriptor == "Button 3" && c.DeviceGuid == A);
            string padLabel = extra.DeviceLabel;

            // No key yet: the source stays where its input was picked.
            extra.Kind = "Incremental";
            Assert.Equal(A, extra.DeviceGuid);
            var up = Key(row, "Button 1", B);
            extra.ParamUpInputChoice = up;
            Assert.Equal(B, extra.DeviceGuid);

            extra.Kind = "Direct";
            Assert.Equal(A, extra.DeviceGuid);

            // The key kind again, no key touched: the controller Up reads.
            extra.Kind = keyKind;
            Assert.Equal(B, extra.DeviceGuid);
            Assert.Equal(up.DeviceLabel, extra.DeviceLabel);
            var saved = extra.ToDomain();
            Assert.Equal((keyKind, B, "Button 1", B), (saved.Kind, saved.DeviceGuid, saved.ParamUp, saved.ParamUpDeviceGuid));

            // With the Xbox controller off, the Wii Remote's key still moves
            // the row, which Step 3 reads on the Wii Remote's pass.
            SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            OnSlot(p.Xbox);
            OnSlot(p.Wii);
            var set = Set("LeftThumbAxisX", saved);
            p.Xbox.IsOnline = false;
            p.Wii.InputState.Buttons[1] = true;
            Assert.True(TimedFrame(set, 0.25, p.Xbox, p.Wii).ThumbLX > 0, "the key's own controller no longer moved the row");
            p.Xbox.IsOnline = true;
            p.Wii.InputState.Buttons[1] = false;
            // And unassigning the Xbox controller leaves the source in place.
            SettingsService.StripDeviceFromSlot(Guid.Parse(A), Slot);
            Assert.Same(saved, Assert.Single(Assert.Single(set.Rows).Sources));

            // And back: each key kind on the keys' controller, each kind that
            // reads the input on the input's.
            extra.Kind = "Toggle";
            Assert.Equal((A, padLabel), (extra.DeviceGuid, extra.DeviceLabel));
            extra.Kind = keyKind == "Ramped" ? "Incremental" : "Ramped";
            Assert.Equal(B, extra.DeviceGuid);
            extra.Kind = keyKind;
            Assert.Equal(B, extra.DeviceGuid);

            // Keys on two controllers: it names none in a key kind, and the
            // input's controller is still remembered.
            extra.ParamDownInputChoice = Key(row, "Button 2", A);
            Assert.Equal("", extra.DeviceGuid);
            extra.Kind = "Direct";
            Assert.Equal(A, extra.DeviceGuid);
            extra.Kind = keyKind;
            Assert.Equal("", extra.DeviceGuid);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Mapping_AnyDevice, extra.DeviceLabel);
            extra.Kind = "Direct";
            Assert.Equal((A, padLabel), (extra.DeviceGuid, extra.DeviceLabel));
        }

        /// <summary>An Invert on Hold source reads its modifier alone, so it
        /// belongs to the controller that key names, as an Incremental or
        /// Ramp source belongs to its keys'. It followed nothing. Set on an
        /// input picked from the Xbox controller, with the Wii Remote's
        /// Button 1 for its modifier, it stayed on the Xbox controller, and
        /// unassigning that controller deleted it while its modifier was
        /// still on the slot. Now the device cleanup judges it by the
        /// controller its modifier reads, and a return to Direct puts it
        /// back where its input was picked. The source here sits on no row:
        /// its input and its keys land as a pick or a recording lands them
        /// (SetParamKey), and PickingAKeyStoresTheControllerItWasPickedFrom
        /// takes the same move through a row's picker.</summary>
        [Fact]
        public void AnInvertOnHoldSourceFollowsItsModifier()
        {
            var extra = new MappingSourceItem { DeviceGuid = A, DeviceLabel = "Xbox Controller", Descriptor = "Button 3" };
            var moves = new List<string>();
            extra.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MappingSourceItem.DeviceGuid)) moves.Add(extra.DeviceGuid);
            };

            // No modifier yet: the source stays where its input was picked.
            extra.Kind = "InvertOnHold";
            Assert.Equal(A, extra.DeviceGuid);
            extra.SetParamKey(MappingSourceItem.ParamRecordTarget.Modifier, "Button 1", B);
            Assert.Equal(B, extra.DeviceGuid);
            // One key landing is one move.
            Assert.Equal(new[] { B }, moves);
            var saved = extra.ToDomain();
            Assert.Equal(("InvertOnHold", B, "Button 1", B), (saved.Kind, saved.DeviceGuid, saved.ParamModifier, saved.ParamModifierDeviceGuid));
            // The kept input and the controller it goes back to are saved.
            Assert.Equal(("Button 3", A), (saved.Descriptor, saved.DescriptorDeviceGuid));

            // Controller B's Button 1 inverts the row, a stick pushed by its
            // Button 2.
            var a = Device(A);
            var b = Device(B);
            var push = new MappingSource { Kind = "Direct", DeviceGuid = B, Descriptor = "Button 2" };
            var set = Set("LeftThumbAxisX", push, saved);
            b.InputState.Buttons[2] = true;
            Assert.True(Frame(set, a, b).ThumbLX > 30000);
            b.InputState.Buttons[1] = true;
            Assert.True(Frame(set, a, b).ThumbLX < -30000);

            // Unassigning controller A leaves the source and its modifier,
            // and takes the kept input that was picked there.
            SettingsService.StripDeviceFromSlot(Guid.Parse(A), Slot);
            Assert.Equal(new[] { push, saved }, Assert.Single(set.Rows).Sources);
            Assert.Equal(("Button 1", B, "", (string)null),
                (saved.ParamModifier, saved.ParamModifierDeviceGuid, saved.Descriptor, saved.DescriptorDeviceGuid));
            Assert.True(Frame(set, b).ThumbLX < -30000, "the modifier no longer inverted the row");

            // Unassigning controller B takes the source with its modifier.
            var again = extra.ToDomain();
            var other = new MappingSource { Kind = "Direct", DeviceGuid = A, Descriptor = "Button 4" };
            set = Set("LeftThumbAxisX", other, again);
            SettingsService.StripDeviceFromSlot(Guid.Parse(B), Slot);
            Assert.Same(other, Assert.Single(Assert.Single(set.Rows).Sources));

            // Direct reads the input where it was picked, under the name it
            // had there, and Invert on Hold again, with no key touched, is
            // where its modifier reads.
            extra.Kind = "Direct";
            Assert.Equal((A, "Xbox Controller"), (extra.DeviceGuid, extra.DeviceLabel));
            Assert.Null(extra.ToDomain().DescriptorDeviceGuid);
            extra.Kind = "InvertOnHold";
            Assert.Equal(B, extra.DeviceGuid);
            Assert.Equal(new[] { B, A, B }, moves);

            // A modifier that reads any controller leaves the source naming none.
            extra.SetParamKey(MappingSourceItem.ParamRecordTarget.Modifier, "Gamepad ButtonA", "");
            Assert.Equal(("", PadForge.Resources.Strings.Strings.Instance.Mapping_AnyDevice), (extra.DeviceGuid, extra.DeviceLabel));
            Assert.Equal(("", A), (extra.ToDomain().DeviceGuid, extra.ToDomain().DescriptorDeviceGuid));
            // A cleared modifier moves nothing.
            extra.SetParamKey(MappingSourceItem.ParamRecordTarget.Modifier, "Button 1", B);
            extra.SetParamKey(MappingSourceItem.ParamRecordTarget.Modifier, "", null);
            Assert.Equal(B, extra.DeviceGuid);
            Assert.Equal(new[] { B, A, B, "", B }, moves);
        }

        /// <summary>The same source one mode further on. Its Up key, picked
        /// from controller B, put it there as an Incremental source. Set to
        /// Invert on Hold with its modifier picked from controller A, it
        /// stayed on B, where no key of its kind reads, and unassigning B
        /// deleted it. The Up key it no longer reads goes with B, and the
        /// source stays with its modifier.</summary>
        [Fact]
        public void AnInvertOnHoldSourceLeavesTheControllerItsUpKeyPutItOn()
        {
            var extra = new MappingSourceItem { DeviceGuid = A, DeviceLabel = "Xbox Controller", Descriptor = "Button 3" };
            extra.Kind = "Incremental";
            extra.SetParamKey(MappingSourceItem.ParamRecordTarget.Up, "Button 1", B);
            Assert.Equal(B, extra.DeviceGuid);

            // No modifier yet: nothing moves.
            extra.Kind = "InvertOnHold";
            Assert.Equal(B, extra.DeviceGuid);
            extra.SetParamKey(MappingSourceItem.ParamRecordTarget.Modifier, "Button 2", A);
            Assert.Equal(A, extra.DeviceGuid);
            var saved = extra.ToDomain();
            // Back on the controller its input was picked on, it saves none for it.
            Assert.Equal(("InvertOnHold", A, A, (string)null),
                (saved.Kind, saved.DeviceGuid, saved.ParamModifierDeviceGuid, saved.DescriptorDeviceGuid));

            var other = new MappingSource { Kind = "Direct", DeviceGuid = A, Descriptor = "Button 4" };
            var set = Set("LeftThumbAxisX", other, saved);
            SettingsService.StripDeviceFromSlot(Guid.Parse(B), Slot);
            Assert.Equal(new[] { other, saved }, Assert.Single(set.Rows).Sources);
            Assert.Equal(("", "Button 2", A), (saved.ParamUp, saved.ParamModifier, saved.ParamModifierDeviceGuid));

            // The kinds move it between the controllers their keys read.
            extra.Kind = "Incremental";
            Assert.Equal(B, extra.DeviceGuid);
            extra.Kind = "InvertOnHold";
            Assert.Equal(A, extra.DeviceGuid);
            extra.Kind = "Ramped";
            Assert.Equal(B, extra.DeviceGuid);
        }

        /// <summary>A key saved before keys carried a controller reads on
        /// its source's (SourceKindRuntime.KeyDevice). When the source moves,
        /// each such key is first given the controller it has read, the keys
        /// its kind leaves unread too. A source that followed its Up key
        /// gave none to its modifier, which then read on the Up key's
        /// controller once the kind was Invert on Hold. These sources sit on
        /// no row, so no list gives their old keys a controller first.</summary>
        [Fact]
        public void AKeySavedWithoutAControllerKeepsReadingThereWhenItsSourceMoves()
        {
            var src = MappingSourceItem.FromDomain(new MappingSource
            {
                Kind = "Incremental", DeviceGuid = A, ParamModifier = "Button 2",
            });
            Assert.Null(src.ParamModifierDeviceGuid);
            Assert.Equal(A, SourceKindRuntime.KeyDevice(src.ToDomain().ParamModifierDeviceGuid, src.ToDomain()));

            src.SetParamKey(MappingSourceItem.ParamRecordTarget.Up, "Button 1", B);
            Assert.Equal((B, A), (src.DeviceGuid, src.ParamModifierDeviceGuid));
            var saved = src.ToDomain();
            Assert.Equal(A, SourceKindRuntime.KeyDevice(saved.ParamModifierDeviceGuid, saved));
            // Invert on Hold reads the modifier, so the source goes back to it.
            src.Kind = "InvertOnHold";
            Assert.Equal(A, src.DeviceGuid);

            // The other way: an Invert on Hold source that follows a newly
            // picked modifier leaves its old Up and Down keys reading where
            // they read.
            var modified = MappingSourceItem.FromDomain(new MappingSource
            {
                Kind = "InvertOnHold", DeviceGuid = A, ParamUp = "Button 5", ParamDown = "Button 6", ParamModifier = "Button 2",
            });
            modified.SetParamKey(MappingSourceItem.ParamRecordTarget.Modifier, "Button 3", B);
            Assert.Equal((B, A, A), (modified.DeviceGuid, modified.ParamUpDeviceGuid, modified.ParamDownDeviceGuid));
            modified.Kind = "Ramped";
            Assert.Equal(A, modified.DeviceGuid);

            // A modifier saved without a controller reads on its source's,
            // so it moves nothing.
            var still = MappingSourceItem.FromDomain(new MappingSource
            {
                Kind = "Direct", DeviceGuid = A, Descriptor = "Button 3", ParamModifier = "Button 2",
            });
            still.Kind = "InvertOnHold";
            Assert.Equal((A, (string)null), (still.DeviceGuid, still.ParamModifierDeviceGuid));
        }

        /// <summary>A row's primary keeps its kind and its keys in a holder
        /// the row loads again and again. A Direct, Toggle or Rapid Trigger
        /// primary, or an unmapped row, loaded over a key source left that
        /// source's controller in the holder, and a primary then set to
        /// Invert on Hold saved it: a controller its modifier never named,
        /// by which the device cleanup judges the source. The holder names
        /// none after such a load or a Clear, and the primary follows its
        /// modifier as any source does. The row here sits on no pad: its
        /// holder is loaded, cleared and edited as the grid does it.</summary>
        [Fact]
        public void APrimarySetToInvertOnHoldSavesTheControllerItsModifierNames()
        {
            string anyDevice = PadForge.Resources.Strings.Strings.Instance.Mapping_AnyDevice;
            var row = new MappingItem("Left Stick X", "LeftThumbAxisX", MappingCategory.LeftStick);
            var holder = row.PrimaryKindSource;
            // Invert on Hold needs a source to invert.
            row.ExtraSources.Add(new MappingSourceItem { DeviceGuid = A, Descriptor = "Button 3" });

            foreach (var next in new[]
            {
                new MappingSource { Kind = "Direct", DeviceGuid = A, Descriptor = "Axis 0" },
                new MappingSource { Kind = "Toggle", DeviceGuid = A, Descriptor = "Button 0" },
                new MappingSource { Kind = "RapidTrigger", DeviceGuid = A, Descriptor = "Axis 2" },
                null,
            })
            {
                // An earlier load: a Ramp primary on a third controller, its
                // holder still under the name the last edit gave it.
                row.LoadPrimaryKind(Ramp(C, "Button 5", C, "Button 6", C));
                holder.DeviceLabel = "Pad C";
                Assert.Equal(C, holder.DeviceGuid);
                row.LoadPrimaryKind(next);
                Assert.Equal(("", ""), (holder.DeviceGuid, holder.DeviceLabel));

                holder.Kind = "InvertOnHold";
                Assert.Equal("InvertOnHold", holder.Kind);
                // What the save writes for a primary that is a kind
                // (SettingsService.SaveViewModelToPadSetting).
                Assert.Equal("", holder.ToDomain().DeviceGuid);
                holder.SetParamKey(MappingSourceItem.ParamRecordTarget.Modifier, "Button 1", B);
                var saved = holder.ToDomain();
                Assert.Equal(("InvertOnHold", B, "Button 1", B),
                    (saved.Kind, saved.DeviceGuid, saved.ParamModifier, saved.ParamModifierDeviceGuid));
            }

            // Clear leaves nothing assigned, the holder's controller and its
            // name included.
            Assert.Equal(B, holder.DeviceGuid);
            row.ClearCommand.Execute(null);
            Assert.Equal(("Direct", ""), (holder.Kind, holder.DeviceGuid));
            holder.Kind = "InvertOnHold";
            holder.SetParamKey(MappingSourceItem.ParamRecordTarget.Modifier, "Button 1", B);
            holder.SetParamKey(MappingSourceItem.ParamRecordTarget.Modifier, "Gamepad ButtonA", "");
            Assert.Equal(("", anyDevice), (holder.DeviceGuid, holder.DeviceLabel));
            row.ClearCommand.Execute(null);
            Assert.Equal(("Direct", "", ""), (holder.Kind, holder.DeviceGuid, holder.DeviceLabel));
        }

        /// <summary>A load moves no Invert on Hold source either. One saved
        /// on a controller other than its modifier's loads there, as an
        /// extra and as a row's primary, whose kind lands after its
        /// modifier. The next edit of its kind puts it where its modifier
        /// reads. The grid's loader runs on a single pad made for the
        /// test.</summary>
        [Fact]
        public void ALoadKeepsAnInvertOnHoldSourceOnTheControllerItWasSavedOn()
        {
            var pad = new PadViewModel(0);
            MappingItem Row(string target) => pad.Mappings.Single(m => m.TargetSettingName == target);
            var saved = new MappingSource
            {
                Kind = "InvertOnHold", DeviceGuid = A, ParamModifier = "Button 3", ParamModifierDeviceGuid = B,
            };
            SettingsManager.SlotMappingSets[pad.PadIndex] = new MappingSet
            {
                Rows =
                {
                    new MappingRow
                    {
                        Target = "LeftTrigger", LayerMask = "Base",
                        Sources = new List<MappingSource> { new() { Kind = "Direct", DeviceGuid = A, Descriptor = "Axis 2" }, saved.Clone() },
                    },
                    new MappingRow
                    {
                        Target = "RightTrigger", LayerMask = "Base",
                        Sources = new List<MappingSource> { saved.Clone(), new() { Kind = "Direct", DeviceGuid = A, Descriptor = "Axis 5" } },
                    },
                },
            };
            InputService.RefreshMappingsToViewModel(pad);

            var extra = Assert.Single(Row("LeftTrigger").ExtraSources);
            Assert.Equal(("InvertOnHold", A, B), (extra.Kind, extra.DeviceGuid, extra.ParamModifierDeviceGuid));
            var primary = Row("RightTrigger").PrimaryKindSource;
            Assert.Equal(("InvertOnHold", A, B), (primary.Kind, primary.DeviceGuid, primary.ParamModifierDeviceGuid));
            Assert.Equal(A, primary.ToDomain().DeviceGuid);

            extra.Kind = "Direct";
            extra.Kind = "InvertOnHold";
            Assert.Equal(B, extra.DeviceGuid);
            primary.Kind = "Ramped";
            primary.Kind = "InvertOnHold";
            Assert.Equal(B, primary.DeviceGuid);
        }

        /// <summary>A load moves no source. A device paste leaves a key
        /// source on the controller it was pasted to with a key still on a
        /// third (InputService.ApplyMultiSourceRowsToCurrentDevice), and the
        /// grid loads it there, as an extra and as a row's primary, whose
        /// kind lands after its keys. The next edit of its kind puts it
        /// where its keys say.</summary>
        [Fact]
        public void ALoadKeepsAKeySourceOnTheControllerItWasSavedOn()
        {
            var p = Populate();
            int slot = p.Pad.PadIndex;
            var copied = new List<MappingRow>
            {
                new()
                {
                    Target = "RightTrigger", LayerMask = "Base",
                    Sources = new List<MappingSource>
                    {
                        new() { Kind = "Direct", DeviceGuid = C, Descriptor = "Axis 5" },
                        Ramp(C, "Button 5", C, "Button 1", B),
                    },
                },
                new()
                {
                    Target = "LeftTrigger", LayerMask = "Base",
                    Sources = new List<MappingSource> { Ramp(C, "Button 5", C, "Button 1", B) },
                },
            };
            InputService.ApplyMultiSourceRowsToCurrentDevice(slot, Guid.Parse(A), copied);
            InputService.RefreshMappingsToViewModel(p.Pad);
            Rebuild(p);

            var extra = Assert.Single(p.Row("RightTrigger").ExtraSources);
            Assert.Equal(("Ramped", A, A, B), (extra.Kind, extra.DeviceGuid, extra.ParamUpDeviceGuid, extra.ParamDownDeviceGuid));
            Assert.Equal(A, extra.ToDomain().DeviceGuid);
            var primary = p.Row("LeftTrigger").PrimaryKindSource;
            Assert.Equal(("Ramped", A, A, B), (primary.Kind, primary.DeviceGuid, primary.ParamUpDeviceGuid, primary.ParamDownDeviceGuid));

            extra.Kind = "Incremental";
            Assert.Equal("", extra.DeviceGuid);
            primary.Kind = "Incremental";
            Assert.Equal("", primary.DeviceGuid);
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
        /// the controller Down was recorded on. A recorded modifier takes
        /// its Invert on Hold source to the controller it fired on, as
        /// that stamp did. With the stamp gone and nothing in its place,
        /// the source stayed where it was.</summary>
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

            // The modifier of an Invert on Hold source, recorded on the Wii
            // Remote: the source goes there with it.
            var modified = new MappingSourceItem { DeviceGuid = A, Descriptor = "Button 3", Kind = "InvertOnHold" };
            row.ExtraSources.Add(modified);
            p.Wii.InputState.Buttons[2] = false;
            recorder.StartRecordingExtraSourceParam(row, modified, 0, RecorderService.ParamTarget.Modifier);
            Assert.True(recorder.IsRecording);
            p.Wii.InputState.Buttons[1] = true;
            try { PollTick.Invoke(recorder, new object[] { null, EventArgs.Empty }); }
            finally { if (recorder.IsRecording) recorder.CancelRecording(); }
            Assert.Equal(("Button 1", B, B), (modified.ParamModifier, modified.ParamModifierDeviceGuid, modified.DeviceGuid));
            Assert.Equal((B, A), (modified.ToDomain().DeviceGuid, modified.ToDomain().DescriptorDeviceGuid));
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

        /// <summary>A source on its keys' controller B whose kept input was
        /// picked on A.</summary>
        private static MappingSource FollowedAway(string picked = A)
        {
            var src = Ramp(B, "Button 5", B, "Button 1", B);
            src.Descriptor = "Button 3";
            src.DescriptorDeviceGuid = picked;
            return src;
        }

        /// <summary>Wherever a source is copied, rekeyed or stripped, the
        /// controller of its kept input goes the way its keys go: a profile
        /// snapshot and a layer copy keep it, a copy to another slot moves
        /// it to that slot's controller, a device paste leaves a third
        /// controller where it is, and where the copy has no such
        /// controller, or the controller is unassigned, the kept input goes
        /// with it.</summary>
        [Fact]
        public void ACopyCarriesTheControllerOfAKeptInput()
        {
            var row = new MappingRow
            {
                Target = "LeftThumbAxisX", LayerMask = "Base", Sources = new List<MappingSource> { FollowedAway() },
            };

            Assert.Equal(A, Assert.Single(InputService.CopyRowSources(row, g => g ?? "", out _)).DescriptorDeviceGuid);
            var snapshot = InputService.CloneMappingSetDeep(new MappingSet { Rows = { row } });
            Assert.Equal(A, snapshot.Rows[0].Sources[0].DescriptorDeviceGuid);

            var moved = Assert.Single(InputService.CopyRowSources(row, g => g == A ? C : g, out _));
            Assert.Equal((C, "Button 3", B), (moved.DescriptorDeviceGuid, moved.Descriptor, moved.DeviceGuid));

            var bare = Assert.Single(InputService.CopyRowSources(row, g => g == A ? null : g, out _));
            Assert.Equal(((string)null, "", "Button 5"), (bare.DescriptorDeviceGuid, bare.Descriptor, bare.ParamUp));
            Assert.Equal((A, "Button 3"), (row.Sources[0].DescriptorDeviceGuid, row.Sources[0].Descriptor));

            SettingsManager.SlotMappingSets[Slot] = new MappingSet();
            InputService.ApplyMultiSourceRowsToCurrentDevice(Slot, Guid.Parse(C), new List<MappingRow> { row });
            var pasted = Assert.Single(SettingsManager.SlotMappingSets[Slot].Rows.Single().Sources);
            Assert.Equal((C, C, A), (pasted.DeviceGuid, pasted.ParamUpDeviceGuid, pasted.DescriptorDeviceGuid));

            var set = Set("LeftThumbAxisX", FollowedAway());
            InputService.RemapDeviceGuidsInSlotMappingSets(new Dictionary<string, string> { [A] = C });
            Assert.Equal((C, B), (set.Rows[0].Sources[0].DescriptorDeviceGuid, set.Rows[0].Sources[0].DeviceGuid));

            set = Set("LeftThumbAxisX", FollowedAway());
            SettingsService.StripDeviceFromSlot(Guid.Parse(A), Slot);
            var left = Assert.Single(Assert.Single(set.Rows).Sources);
            Assert.Equal(((string)null, "", B, "Button 5"), (left.DescriptorDeviceGuid, left.Descriptor, left.DeviceGuid, left.ParamUp));
        }

        /// <summary>Two sources that differ only in the controller their kept
        /// input goes back to are two sources, and the load keeps both. Two
        /// alike in that too are one.</summary>
        [Fact]
        public void TheLoadKeepsTwoSourcesThatDifferOnlyInTheirKeptInputsController()
        {
            var set = Set("LeftThumbAxisX", FollowedAway(A), FollowedAway(C), FollowedAway(C), FollowedAway(null));
            SettingsService.SanitizeMappingSet(set, Slot);
            Assert.Equal(new[] { A, C, null }, set.Rows[0].Sources.Select(s => s.DescriptorDeviceGuid));
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

        /// <summary>Consume Mapped Inputs swallows the keys the row reads
        /// and none a source only kept. On a stick row a steering kind reads
        /// its Descriptor and ParamYDescriptor as one stick, and a motion
        /// kind reads gravity or the shake envelope, neither. On a trigger or
        /// D-pad row both fall to the Direct read of the Descriptor, and
        /// nothing reads ParamYDescriptor. A row the target resolver files as
        /// a button may be a keyboard-and-mouse, Extended or touchpad stick,
        /// so it keeps what either read takes. MotionLeanX swallowed both
        /// keys on every row, and the other motion kinds their Descriptor on
        /// a stick row: keys no mapping read never reached the foreground
        /// application.</summary>
        [Theory]
        [InlineData("MotionLeanX", "LeftThumbAxisX", false, false)]
        [InlineData("MotionLeanAuxX", "LeftThumbAxisX", false, false)]
        [InlineData("MotionShake", "LeftThumbAxisX", false, false)]
        [InlineData("MotionShakeAux", "MotionPitch", false, false)]
        [InlineData("MotionLeanX", "LeftTrigger", true, false)]
        [InlineData("MotionShake", "LeftTrigger", true, false)]
        [InlineData("MotionLeanX", "DPadUp", true, false)]
        [InlineData("MotionLeanX", "KbmMouseX", true, false)]
        [InlineData("WindingStick", "LeftThumbAxisX", true, true)]
        [InlineData("AngleToAxisX", "LeftThumbAxisX", true, true)]
        [InlineData("AngleToAxisY", "LeftTrigger", true, false)]
        [InlineData("WindingStick", "KbmMouseX", true, true)]
        [InlineData("Direct", "LeftThumbAxisX", true, false)]
        [InlineData("Toggle", "LeftTrigger", true, false)]
        [InlineData("Incremental", "LeftThumbAxisX", false, false)]
        public void TheConsumedKeysFollowWhatTheRowReads(string kind, string target, bool descriptor, bool paramY)
        {
            var keyboard = Device(A, InputDeviceType.Keyboard);
            Set(target, new MappingSource { Kind = kind, DeviceGuid = A, Descriptor = "Button 65", ParamYDescriptor = "Button 66" });
            var collect = typeof(InputService).GetMethod("CollectSuppressedInputs", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(collect);
            var keys = new HashSet<int>();
            collect.Invoke(null, new object[] { keyboard, keys, new HashSet<int>() });
            Assert.Equal(descriptor, keys.Contains(65));
            Assert.Equal(paramY, keys.Contains(66));
        }

        /// <summary>A key that alone triggers a macro on the keyboard's slot
        /// is consumed with the mapped ones, and a chord's keys are not. A
        /// macro pad key typed itself before its macro ran (discussion
        /// #537).</summary>
        [Fact]
        public void AKeyThatAloneTriggersAMacroIsConsumed()
        {
            var keyboard = Device(A, InputDeviceType.Keyboard);
            var id = Guid.Parse(A);
            var macros = new[]
            {
                new MacroItem { TriggerDeviceGuid = id, TriggerRawButtons = new[] { 65 } },
                new MacroItem { TriggerDeviceGuid = id, TriggerRawButtons = new[] { 17, 112 } },
            };
            var keys = new HashSet<int>();
            InputService.CollectMacroTriggerKeys(keyboard, slot => slot == Slot ? macros : null, keys, new HashSet<int>());
            Assert.Equal(new[] { 65 }, keys.ToArray());
        }
    }
}
