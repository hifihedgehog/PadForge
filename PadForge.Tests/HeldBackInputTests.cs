using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    /// What Step 3 holds back while an activator reads an input with "Also
    /// Fire Activator's Own Mapping" off, or a consuming macro eats it
    /// (InputManager.IsSourceSuppressedPostpone): what the rows read. A
    /// source is held back on the input its kind reads, and an Up or Down key
    /// on that input reads released, as a Direct source and an Invert on
    /// Hold modifier on it do. The Descriptor an Incremental or Ramp source
    /// kept from an earlier kind is no input. An activator on it dropped the
    /// source from its row, and the lanes that pick a source by its
    /// Descriptor read it as their own.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class HeldBackInputTests : IDisposable
    {
        private const int Slot = 6;
        private const string A = "a6a6a6a6-0000-0000-0000-0000000000a6";
        private const string B = "b6b6b6b6-0000-0000-0000-0000000000b6";

        /// <summary>The input an activator reads, and the Descriptor a key
        /// source kept from an earlier kind.</summary>
        private const int Kept = 7;

        /// <summary>A key source's Up key.</summary>
        private const int Key = 5;

        /// <summary>An input no activator reads.</summary>
        private const int Free = 9;

        private readonly Func<string, string, int, bool> _savedKeys = SourceCoercion.KeyHeldProvider;
        private readonly Func<int, string, string, bool> _savedHeldBack = SourceCoercion.KeyHeldBackProvider;
        private readonly Func<(float normX, float normY)> _savedCursor = SourceCoercion.MouseCursorProvider;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly bool[] _enabled = (bool[])SettingsManager.SlotEnabled.Clone();
        private readonly double _savedDelta = FrameDeltas[Slot];

        public HeldBackInputTests()
        {
            // The app's own wiring (InputService).
            SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            SourceCoercion.KeyHeldBackProvider = InputManager.KeyHeldBack;
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SettingsManager.SlotCreated[Slot] = true;
            SettingsManager.SlotEnabled[Slot] = true;
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            InputManager.ClearSourceKindRuntime();
        }

        public void Dispose()
        {
            SourceCoercion.KeyHeldProvider = _savedKeys;
            SourceCoercion.KeyHeldBackProvider = _savedHeldBack;
            SourceCoercion.MouseCursorProvider = _savedCursor;
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            InputManager.ClearSourceKindRuntime();
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.SlotMappingSets = _sets;
            Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
            Array.Copy(_enabled, SettingsManager.SlotEnabled, _enabled.Length);
            FrameDeltas[Slot] = _savedDelta;
        }

        // ── Setup ───────────────────────────────────────────────────────

        private static double[] FrameDeltas => (double[])typeof(InputManager)
            .GetField("_currentFrameDelta", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

        private static readonly MethodInfo BeginFrame = typeof(InputManager)
            .GetMethod("BeginFrameMultiSourceTracking", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo Apply = typeof(InputManager)
            .GetMethod("ApplyMappingSetToGamepad", BindingFlags.NonPublic | BindingFlags.Static);

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

        /// <summary>Holds exactly <paramref name="buttons"/> on the
        /// device.</summary>
        private static void Hold(UserDevice ud, params int[] buttons)
        {
            Array.Clear(ud.InputState.Buttons);
            foreach (int b in buttons) ud.InputState.Buttons[b] = true;
        }

        private static string Button(int n) => "Button " + n;

        /// <summary>A Hold activator on the device's input, which lets the
        /// Base rows through, so the rows under test read on.</summary>
        private static ShiftActivator Activator(int input, string device, bool alsoFire = false) => new()
        {
            DeviceGuid = device, Descriptor = Button(input), LayerMask = "Held", LayerName = "Held",
            Mode = "Hold", Kind = "Button", PostponeMapping = alsoFire, InheritUnmapped = true,
        };

        /// <summary>An Incremental or Ramp source on controller A, Up on
        /// <paramref name="up"/>, that kept <paramref name="kept"/> as its
        /// Descriptor from the kind it had before. Full travel in one second,
        /// sticky, a Ramp with no attack or release time.</summary>
        private static MappingSource KeySource(string kind, string kept = null, int up = Key,
            bool invert = false, double rate = 1) => new()
        {
            Kind = kind, DeviceGuid = A, Descriptor = kept ?? Button(Kept), Invert = invert,
            ParamUp = Button(up), ParamUpDeviceGuid = A,
            ParamRate = rate, ParamMin = 0, ParamMax = 1, ParamSticky = true,
            ParamAttackTime = 0, ParamReleaseTime = 0, ParamAutocenter = true,
        };

        private static MappingSource Direct(string descriptor, string device = A, bool invert = false)
            => new() { Kind = "Direct", DeviceGuid = device, Descriptor = descriptor, Invert = invert };

        /// <summary>One row, and an activator on controller A's
        /// <see cref="Kept"/>.</summary>
        private static MappingSet Row(string target, params MappingSource[] sources)
            => Row(target, "", sources);

        private static MappingSet Row(string target, string combine, params MappingSource[] sources)
        {
            var set = new MappingSet();
            set.Rows.Add(new MappingRow
            {
                Target = target, LayerMask = "Base", CombineMode = combine, Sources = sources.ToList(),
            });
            set.ShiftActivators.Add(Activator(Kept, A));
            SettingsManager.SlotMappingSets[Slot] = set;
            return set;
        }

        /// <summary>One frame: Step 3's pass for each device, merged as Step
        /// 4 merges them, at the frame time given.</summary>
        private static Gamepad Frame(MappingSet set, double dt, params UserDevice[] devices)
        {
            BeginFrame.Invoke(null, null);
            FrameDeltas[Slot] = dt;
            var merged = new Gamepad();
            foreach (var ud in devices)
            {
                object[] args = { ud.InputState, set, ud.InstanceGuidString, 50, Slot, new Gamepad() };
                Apply.Invoke(null, args);
                var gp = (Gamepad)args[5];
                if (Math.Abs((int)gp.ThumbLX) > Math.Abs((int)merged.ThumbLX)) merged.ThumbLX = gp.ThumbLX;
                if (gp.LeftTrigger > merged.LeftTrigger) merged.LeftTrigger = gp.LeftTrigger;
                merged.Buttons |= gp.Buttons;
            }
            return merged;
        }

        /// <summary>A frame for the per-target evaluators, which read the
        /// activators the device's pass resolved.</summary>
        private static void Begin(MappingSet set, UserDevice ud, double dt = 1.0)
        {
            BeginFrame.Invoke(null, null);
            FrameDeltas[Slot] = dt;
            InputManager.ResolveActiveLayerMask(Slot, set, ud.InputState, ud.InstanceGuidString);
        }

        public static TheoryData<string> KeyKinds() => new() { "Incremental", "Ramped" };

        // ── The rule ────────────────────────────────────────────────────

        /// <summary>Whether a lane reads the Descriptor of a source of
        /// <paramref name="kind"/>, stated apart from the code: every kind
        /// reads it but the two key kinds, which read their Up and Down keys,
        /// and on a stick lane the motion kinds, which read gravity or the
        /// shake envelope.</summary>
        private static bool LaneReadsTheDescriptor(string kind, bool stickRead) => kind switch
        {
            "Incremental" or "Ramped" => false,
            "MotionLeanX" or "MotionLeanAuxX" or "MotionShake" or "MotionShakeAux" => !stickRead,
            _ => true,
        };

        public static TheoryData<string, bool> EveryKindOnEveryLane()
        {
            var data = new TheoryData<string, bool>();
            foreach (string kind in new[]
            {
                "Direct", "", "Toggle", "RapidTrigger", "Unknown", "WindingStick", "AngleToAxisX", "AngleToAxisY",
                "MotionLeanX", "MotionLeanAuxX", "MotionShake", "MotionShakeAux", "Incremental", "Ramped",
                "InvertOnHold",
            })
                foreach (bool stickRead in new[] { false, true })
                    data.Add(kind, stickRead);
            return data;
        }

        /// <summary>The hold-back test of a source weighs its Descriptor only
        /// where its kind reads it on the lane, and there it answers exactly
        /// as the test of the descriptor alone always has: for an activator
        /// pinned to A, one with no device of its own, a "Gamepad ..." name
        /// for the same input, and an input no activator reads, on a source
        /// pinned to either controller or to none.</summary>
        [Theory]
        [MemberData(nameof(EveryKindOnEveryLane))]
        public void TheHoldBackWeighsTheDescriptorWhereTheKindReadsIt(string kind, bool stickRead)
        {
            var a = Device(A);
            var set = new MappingSet();
            set.ShiftActivators.Add(Activator(Kept, A));
            set.ShiftActivators.Add(Activator(8, ""));
            SettingsManager.SlotMappingSets[Slot] = set;
            Hold(a, Kept, 8);
            InputManager.ResolveActiveLayerMask(Slot, set, a.InputState, A);

            int heldBack = 0;
            foreach (string descriptor in new[] { Button(Kept), Button(8), Button(Free), "Gamepad ButtonA", "" })
                foreach (string device in new[] { A, B, "" })
                {
                    var src = new MappingSource { Kind = kind, DeviceGuid = device, Descriptor = descriptor };
                    bool before = InputManager.IsSourceSuppressedPostpone(Slot, device, descriptor);
                    if (before) heldBack++;
                    bool expected = LaneReadsTheDescriptor(kind, stickRead) && before;
                    Assert.True(expected == InputManager.IsSourceSuppressedPostpone(Slot, src, stickRead),
                        $"{kind} on the {(stickRead ? "stick" : "button or trigger")} lane, '{descriptor}' on '{device}'");
                }
            // The cases are no empty set: the activators hold several back.
            Assert.True(heldBack >= 4, "held back " + heldBack);
        }

        // ── A kind that reads its input is held back as before ─────────

        public static TheoryData<string, string> KindsThatReadTheirInput()
        {
            var data = new TheoryData<string, string>();
            string[] everyLane = { "Direct", null, "Toggle", "RapidTrigger", "Zzz" };
            string[] notOnAStick =
            {
                "WindingStick", "AngleToAxisX", "AngleToAxisY",
                "MotionLeanX", "MotionLeanAuxX", "MotionShake", "MotionShakeAux",
            };
            foreach (string lane in new[]
            {
                "button loop", "trigger loop", "button builder", "trigger builder",
                "button row", "trigger row", "trigger preview", "trim gate",
            })
            {
                foreach (string kind in everyLane) data.Add(lane, kind);
                foreach (string kind in notOnAStick) data.Add(lane, kind);
            }
            foreach (string lane in new[] { "stick loop", "stick builder", "stick pair leg", "stick row", "touchpad row", "trim stick" })
                foreach (string kind in everyLane) data.Add(lane, kind);
            return data;
        }

        /// <summary>A source whose kind reads its input on a lane is held back
        /// exactly as a Direct source on that input is, to the bit, which is
        /// how every source was held back before: the test weighed the
        /// Descriptor whatever the kind. With "Also Fire Activator's Own
        /// Mapping" on, the same source fires, so the input it reads is live.
        /// On a button or trigger lane the steering and motion kinds read
        /// their input as the Direct read does. On a stick lane they read a
        /// stick, gravity or the shake envelope, and the rule's own test
        /// covers them (TheHoldBackWeighsTheDescriptorWhereTheKindReadsIt).
        /// The lanes are every site the rule reads: the gamepad loops, the
        /// builders and the pair's second leg, the per-target rows, the
        /// touchpad row, the Triggers tab preview, and the stick trim's gate
        /// and trim stick. The preview reads a Toggle source with no latch,
        /// at rest, so there the press shows nothing either way.</summary>
        [Theory]
        [MemberData(nameof(KindsThatReadTheirInput))]
        public void AKindThatReadsItsInputIsHeldBackAsBefore(string lane, string kind)
        {
            var a = Device(A);
            string heldBack = ReadLane(lane, kind, a, alsoFire: false);
            string direct = ReadLane(lane, "Direct", a, alsoFire: false);
            string fired = ReadLane(lane, kind, a, alsoFire: true);
            Assert.Equal(direct, heldBack);
            if (lane != "trigger preview" || kind != "Toggle")
                Assert.NotEqual(fired, heldBack);
        }

        /// <summary>What <paramref name="lane"/> makes of one source of
        /// <paramref name="kind"/> on the activator's input in the frame the
        /// input is pressed, after a frame with it released, as text that
        /// carries every output the lane writes. Button 3 holds a stick trim's
        /// gate, and its trim stick rests centered.</summary>
        private static string ReadLane(string lane, string kind, UserDevice a, bool alsoFire)
        {
            InputManager.ClearAllShiftRuntime();
            InputManager.ClearSourceKindRuntime();
            var set = LaneRow(lane, kind);
            set.ShiftActivators[0].PostponeMapping = alsoFire;
            Hold(a, 3);
            RunLane(lane, set, a);
            Hold(a, Kept, 3);
            return RunLane(lane, set, a);
        }

        private static MappingSet LaneRow(string lane, string kind)
        {
            var src = new MappingSource { Kind = kind, DeviceGuid = A, Descriptor = Button(Kept) };
            return lane switch
            {
                "button loop" or "button row" => Row("ButtonA", src),
                "trigger loop" or "trigger row" => Row("LeftTrigger", src),
                "stick loop" or "stick row" => Row("LeftThumbAxisX", src),
                "button builder" => Row("ButtonA", src, Direct(Button(Free))),
                "trigger builder" => Row("LeftTrigger", src, Direct(Button(Free))),
                "stick builder" => Row("LeftThumbAxisX", src, Direct(Button(Free))),
                "stick pair leg" => Row("LeftThumbAxisX", Direct(Button(Free)), Invert(src)),
                "touchpad row" => Row("TouchpadX1", src),
                "trigger preview" => Row("LeftTrigger", "Average", src, Direct(Button(Free))),
                "trim gate" => Row("LeftTrigger", "StickTrim", src, Direct("Axis 0")),
                "trim stick" => Row("LeftTrigger", "StickTrim", Direct(Button(3)), src),
                _ => throw new ArgumentException(lane),
            };
        }

        private static string RunLane(string lane, MappingSet set, UserDevice a)
        {
            switch (lane)
            {
                case "button row":
                    Begin(set, a);
                    return InputManager.TryEvaluateMappingSetButton(a.InputState, set, A, Slot, "ButtonA", 50, out bool pressed)
                        + " " + pressed;
                case "trigger row":
                    Begin(set, a);
                    return InputManager.TryEvaluateMappingSetRawTrigger(a.InputState, set, A, Slot, "LeftTrigger", out short pull)
                        + " " + pull;
                case "stick row":
                    Begin(set, a);
                    return InputManager.TryEvaluateMappingSetBipolarAxis(a.InputState, set, A, Slot, "LeftThumbAxisX", out short axis)
                        + " " + axis;
                case "touchpad row":
                    Begin(set, a);
                    return InputManager.TryEvaluateMappingSetTouchpadAxis(a.InputState, set, A, Slot, "TouchpadX1", 0, out short x)
                        + " " + x;
                case "trigger preview":
                    Begin(set, a);
                    return InputManager.EvaluatePerDeviceTriggerPreview(a.InputState, set, A, "LeftTrigger", Slot).ToString();
                default:
                    var gp = Frame(set, 1.0, a);
                    return gp.Buttons + " " + gp.LeftTrigger + " " + gp.ThumbLX;
            }
        }

        private static MappingSource Invert(MappingSource src)
        {
            src.Invert = true;
            return src;
        }

        // ── Each lane reads a key source that kept an activator's input ─

        /// <summary>The gamepad's button loop. The Direct source on the
        /// activator's input stays held back.</summary>
        [Fact]
        public void TheButtonLoop_ReadsAKeySourceWhoseKeptInputAnActivatorHolds()
        {
            var a = Device(A);
            Hold(a, Kept, Key);
            Assert.True(Frame(Row("ButtonA", KeySource("Incremental")), 1.0, a).IsButtonPressed(Gamepad.A));
            Assert.False(Frame(Row("ButtonA", Direct(Button(Kept))), 1.0, a).IsButtonPressed(Gamepad.A));
        }

        [Theory]
        [MemberData(nameof(KeyKinds))]
        public void TheStickLoop_ReadsAKeySourceWhoseKeptInputAnActivatorHolds(string kind)
        {
            var a = Device(A);
            Hold(a, Kept, Key);
            Assert.True(Frame(Row("LeftThumbAxisX", KeySource(kind)), 1.0, a).ThumbLX > 30000);
            Assert.Equal(0, Frame(Row("LeftThumbAxisX", Direct(Button(Kept))), 1.0, a).ThumbLX);
        }

        [Theory]
        [MemberData(nameof(KeyKinds))]
        public void TheTriggerLoop_ReadsAKeySourceWhoseKeptInputAnActivatorHolds(string kind)
        {
            var a = Device(A);
            Hold(a, Kept, Key);
            Assert.Equal(ushort.MaxValue, Frame(Row("LeftTrigger", KeySource(kind)), 1.0, a).LeftTrigger);
            Assert.Equal(0, Frame(Row("LeftTrigger", Direct(Button(Kept))), 1.0, a).LeftTrigger);
        }

        /// <summary>The builders a row of several sources takes, the stick's
        /// pair of a source and its opposite included, on either leg.</summary>
        [Theory]
        [MemberData(nameof(KeyKinds))]
        public void TheBuilders_ReadAKeySourceWhoseKeptInputAnActivatorHolds(string kind)
        {
            var a = Device(A);
            Hold(a, Kept, Key);
            if (kind == "Incremental")
                Assert.True(Frame(Row("ButtonA", KeySource(kind), Direct(Button(Free))), 1.0, a).IsButtonPressed(Gamepad.A));
            Assert.Equal(ushort.MaxValue, Frame(Row("LeftTrigger", KeySource(kind), Direct(Button(Free))), 1.0, a).LeftTrigger);
            Assert.True(Frame(Row("LeftThumbAxisX", KeySource(kind), Direct(Button(Free))), 1.0, a).ThumbLX > 30000);

            // The key source as the pair's first leg, and as its second.
            InputManager.ClearSourceKindRuntime();
            Assert.True(Frame(Row("LeftThumbAxisX", KeySource(kind), Direct(Button(Free), invert: true)), 1.0, a).ThumbLX > 30000);
            InputManager.ClearSourceKindRuntime();
            Assert.True(Frame(Row("LeftThumbAxisX", Direct(Button(Free)), KeySource(kind, invert: true)), 1.0, a).ThumbLX < -30000);

            // Positive control: a Direct leg on the activator's input stays
            // held back.
            Assert.Equal(0, Frame(Row("LeftTrigger", Direct(Button(Kept)), Direct(Button(Free))), 1.0, a).LeftTrigger);
        }

        /// <summary>The per-target evaluators the other controller types
        /// read through, a row of one source.</summary>
        [Theory]
        [MemberData(nameof(KeyKinds))]
        public void ThePerTargetEvaluators_ReadAKeySourceWhoseKeptInputAnActivatorHolds(string kind)
        {
            var a = Device(A);
            Hold(a, Kept, Key);

            var trigger = Row("LeftTrigger", KeySource(kind));
            Begin(trigger, a);
            Assert.True(InputManager.TryEvaluateMappingSetRawTrigger(a.InputState, trigger, A, Slot, "LeftTrigger", out short pull));
            Assert.Equal(short.MaxValue, pull);

            var stick = Row("LeftThumbAxisX", KeySource(kind));
            Begin(stick, a);
            Assert.True(InputManager.TryEvaluateMappingSetBipolarAxis(a.InputState, stick, A, Slot, "LeftThumbAxisX", out short axis));
            Assert.True(axis > 30000, "stick " + axis);

            if (kind == "Incremental")
            {
                var button = Row("ButtonA", KeySource(kind));
                Begin(button, a);
                Assert.True(InputManager.TryEvaluateMappingSetButton(a.InputState, button, A, Slot, "ButtonA", 50, out bool pressed));
                Assert.True(pressed);
            }

            // Positive control.
            var direct = Row("LeftTrigger", Direct(Button(Kept)));
            Begin(direct, a);
            Assert.True(InputManager.TryEvaluateMappingSetRawTrigger(a.InputState, direct, A, Slot, "LeftTrigger", out pull));
            Assert.Equal(short.MinValue, pull);
        }

        /// <summary>The touchpad passthrough row. A key source counts as a
        /// source whose input never lifts, as every source that is no touch
        /// position does: its value rests at zero by itself. Its kept
        /// Descriptor gated it on a finger it never reads.</summary>
        [Theory]
        [MemberData(nameof(KeyKinds))]
        public void TheTouchpadRow_ReadsAKeySourceWhoseKeptInputAnActivatorHolds(string kind)
        {
            var a = Device(A);
            Hold(a, Kept, Key);
            var set = Row("TouchpadX1", KeySource(kind));
            Begin(set, a);
            Assert.True(InputManager.TryEvaluateMappingSetTouchpadAxis(a.InputState, set, A, Slot, "TouchpadX1", 0, out short value));
            Assert.Equal(short.MaxValue, value);

            // A kept touch position, no finger down.
            InputManager.ClearSourceKindRuntime();
            var kept = Row("TouchpadX1", KeySource(kind, kept: "Touchpad 0 Finger 0 X"));
            Hold(a, Key);
            Begin(kept, a);
            Assert.True(InputManager.TryEvaluateMappingSetTouchpadAxis(a.InputState, kept, A, Slot, "TouchpadX1", 0, out value));
            Assert.Equal(short.MaxValue, value);
        }

        /// <summary>The Triggers tab's preview. It reads a key source as at
        /// rest and holds a source back as the row does, so a row of a key
        /// source at rest and a pulled button averages to half, as the row
        /// itself does. The held-back kept Descriptor took the key source out
        /// of the average and showed a full pull.</summary>
        [Theory]
        [MemberData(nameof(KeyKinds))]
        public void TheTriggerPreview_ReadsAKeySourceWhoseKeptInputAnActivatorHolds(string kind)
        {
            var a = Device(A);
            Hold(a, Kept, Free);
            var set = Row("LeftTrigger", "Average", KeySource(kind), Direct(Button(Free)));
            Assert.Equal(32767, Frame(set, 1.0, a).LeftTrigger);
            Begin(set, a);
            Assert.Equal(32767, InputManager.EvaluatePerDeviceTriggerPreview(a.InputState, set, A, "LeftTrigger", Slot));
        }

        /// <summary>The stick-trim row: a key source as the gate, and as the
        /// trim stick, which lowers the stored level.</summary>
        [Theory]
        [MemberData(nameof(KeyKinds))]
        public void TheStickTrim_ReadsAKeySourceWhoseKeptInputAnActivatorHolds(string kind)
        {
            var a = Device(A);
            Hold(a, Kept, Key);
            Assert.Equal(ushort.MaxValue,
                Frame(Row("LeftTrigger", "StickTrim", KeySource(kind), Direct("Axis 0")), 1.0, a).LeftTrigger);

            // Full trim at 100 percent a second for half a second takes half
            // the level off.
            InputManager.ClearSourceKindRuntime();
            Hold(a, Kept, Key, Free);
            Assert.Equal(32767,
                Frame(Row("LeftTrigger", "StickTrim", Direct(Button(Free)), KeySource(kind, rate: 2)), 0.5, a).LeftTrigger);
        }

        /// <summary>The combined D-pad row reads every source's Descriptor
        /// as a hat. A key source's is no input: its kept hat pressed the
        /// D-pad.</summary>
        [Fact]
        public void TheCombinedDPadRow_ReadsNoKeySourcesKeptHat()
        {
            var a = Device(A);
            a.InputState.Povs[0] = 0;   // up
            Hold(a, Key);
            Assert.Equal(0, Frame(Row("DPad", KeySource("Incremental", kept: "POV 0")), 1.0, a).Buttons & Gamepad.DPAD_UP);
            Assert.NotEqual(0, Frame(Row("DPad", Direct("POV 0")), 1.0, a).Buttons & Gamepad.DPAD_UP);
        }

        /// <summary>The Triggers tab preview counts a held-back source as
        /// the live row counts it: at rest, in its place in the combine. It
        /// left the source out, so a row that averages a held-back input with
        /// a pressed one previewed a full pull where the trigger pulled half.
        /// A formula row is previewed by its formula, each source under the
        /// letter of its place: it was combined as the strongest input, so
        /// it previewed a value the trigger never took.</summary>
        [Theory]
        [InlineData("Average", "", 32767)]
        [InlineData("MaxAbs", "", 65535)]
        [InlineData("Sum", "", 65535)]
        [InlineData("Custom", "b", 65535)]
        [InlineData("Custom", "a", 0)]
        [InlineData("Custom", "b * 0.5", 32767)]
        [InlineData("Custom", "a + b * 0.25", 16383)]
        public void TheTriggerPreview_CountsAHeldBackSourceAsTheLiveRowDoes(string combine, string formula, int pull)
        {
            var a = Device(A);
            Hold(a, Kept, Free);
            var set = Row("LeftTrigger", combine, Direct(Button(Kept)), Direct(Button(Free)));
            set.Rows[0].CombineExpression = formula;
            ushort live = Frame(set, 1.0, a).LeftTrigger;
            Begin(set, a);
            ushort preview = InputManager.EvaluatePerDeviceTriggerPreview(a.InputState, set, A, "LeftTrigger", Slot);
            Assert.Equal(pull, live);
            Assert.Equal(live, preview);
        }

        /// <summary>The Triggers tab preview turns the pull over while the
        /// row's Invert on Hold modifier is held, as the trigger does. It
        /// showed the pull as it was, so a held modifier previewed a trigger
        /// at rest where the trigger was pulled, and the reverse.</summary>
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void TheTriggerPreview_FollowsTheRowsInvertOnHoldModifier(bool modifierHeld, bool inputHeld)
        {
            var a = Device(A);
            var set = new MappingSet();
            set.Rows.Add(new MappingRow
            {
                Target = "LeftTrigger", LayerMask = "Base", CombineMode = "",
                Sources = new[]
                {
                    Direct(Button(Free)),
                    new MappingSource { Kind = "InvertOnHold", DeviceGuid = A, ParamModifier = Button(Key) },
                }.ToList(),
            });
            SettingsManager.SlotMappingSets[Slot] = set;
            Array.Clear(a.InputState.Buttons);
            a.InputState.Buttons[Key] = modifierHeld;
            a.InputState.Buttons[Free] = inputHeld;

            ushort live = Frame(set, 1.0, a).LeftTrigger;
            Begin(set, a);
            ushort preview = InputManager.EvaluatePerDeviceTriggerPreview(a.InputState, set, A, "LeftTrigger", Slot);
            Assert.Equal(inputHeld != modifierHeld ? 65535 : 0, live);
            Assert.Equal(live, preview);
        }

        /// <summary>The same on a row that combines two inputs.</summary>
        [Fact]
        public void TheTriggerPreview_FollowsTheModifierOnARowOfTwoInputs()
        {
            var a = Device(A);
            var set = new MappingSet();
            set.Rows.Add(new MappingRow
            {
                Target = "LeftTrigger", LayerMask = "Base", CombineMode = "Average",
                Sources = new[]
                {
                    Direct(Button(Free)),
                    Direct(Button(3)),
                    new MappingSource { Kind = "InvertOnHold", DeviceGuid = A, ParamModifier = Button(Key) },
                }.ToList(),
            });
            SettingsManager.SlotMappingSets[Slot] = set;

            Hold(a, Free, 3, Key);
            Assert.Equal(0, Frame(set, 1.0, a).LeftTrigger);
            Begin(set, a);
            Assert.Equal(0, InputManager.EvaluatePerDeviceTriggerPreview(a.InputState, set, A, "LeftTrigger", Slot));

            Hold(a, Key);
            Assert.Equal(65535, Frame(set, 1.0, a).LeftTrigger);
            Begin(set, a);
            Assert.Equal(65535, InputManager.EvaluatePerDeviceTriggerPreview(a.InputState, set, A, "LeftTrigger", Slot));
        }

        /// <summary>A formula row with sources on two controllers: each
        /// controller's preview reads the formula with the other's sources at
        /// rest, under their own letters, and a controller with no source on
        /// the row previews nothing.</summary>
        [Fact]
        public void TheTriggerPreview_KeepsAFormulasLettersAcrossControllers()
        {
            var a = Device(A);
            var b = Device(B);
            Hold(a, Free);
            Hold(b, Free);
            var set = Row("LeftTrigger", "Custom", Direct(Button(Free), A), Direct(Button(Free), B));
            set.Rows[0].CombineExpression = "a * 0.25 + b * 0.5";
            Begin(set, a);
            Assert.Equal(16383, InputManager.EvaluatePerDeviceTriggerPreview(a.InputState, set, A, "LeftTrigger", Slot));
            Assert.Equal(32767, InputManager.EvaluatePerDeviceTriggerPreview(b.InputState, set, B, "LeftTrigger", Slot));

            var onlyA = Row("LeftTrigger", "Custom", Direct(Button(Free), A), Direct(Button(Key), A));
            onlyA.Rows[0].CombineExpression = "a + b";
            Begin(onlyA, b);
            Assert.Equal(0, InputManager.EvaluatePerDeviceTriggerPreview(b.InputState, onlyA, B, "LeftTrigger", Slot));
        }

        /// <summary>One stick row with a steering source on controller A's
        /// stick, X on Axis 0 and Y on Axis 1, and a Hold activator read as a
        /// button on <paramref name="activatorOn"/>.</summary>
        private static MappingSet SteeringRow(MappingSource steer, string activatorOn, bool alsoFire = false)
        {
            var set = new MappingSet();
            set.Rows.Add(new MappingRow
            {
                Target = "LeftThumbAxisX", LayerMask = "Base", CombineMode = "",
                Sources = new[] { steer }.ToList(),
            });
            set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = A, Descriptor = activatorOn, LayerMask = "Held", LayerName = "Held",
                Mode = "Hold", Kind = "Button", PostponeMapping = alsoFire, InheritUnmapped = true,
            });
            SettingsManager.SlotMappingSets[Slot] = set;
            return set;
        }

        /// <summary>A steering kind reads two inputs as one stick on the
        /// stick lane, its Descriptor and its ParamYDescriptor. An activator
        /// on either holds the source back there. The test weighed the
        /// Descriptor alone, so an activator on the stick's second axis let
        /// the source steer on. On a button or trigger lane the kind reads
        /// its Descriptor alone, as a Direct source does, and the second
        /// input holds nothing back. A kind that reads one input ignores a
        /// second one kept from an earlier kind.</summary>
        [Theory]
        [InlineData("WindingStick")]
        [InlineData("AngleToAxisX")]
        [InlineData("AngleToAxisY")]
        public void ASteeringSourceIsHeldBackByAnActivatorOnEitherOfItsTwoInputs(string kind)
        {
            var a = Device(A);
            var steer = new MappingSource { Kind = kind, DeviceGuid = A, Descriptor = "Axis 0", ParamYDescriptor = "Axis 1" };
            a.InputState.Axis[1] = 65535;   // the activator's input, held
            Frame(SteeringRow(steer, activatorOn: "Axis 1"), 1.0, a);

            Assert.True(InputManager.IsSourceSuppressedPostpone(Slot, steer, stickRead: true));
            Assert.False(InputManager.IsSourceSuppressedPostpone(Slot, steer, stickRead: false));
            var direct = new MappingSource { Kind = "Direct", DeviceGuid = A, Descriptor = "Axis 0", ParamYDescriptor = "Axis 1" };
            Assert.False(InputManager.IsSourceSuppressedPostpone(Slot, direct, stickRead: true));
            // The first input holds the source back on every lane, as before.
            var onFirst = new MappingSource { Kind = kind, DeviceGuid = A, Descriptor = "Axis 1", ParamYDescriptor = "Axis 0" };
            Assert.True(InputManager.IsSourceSuppressedPostpone(Slot, onFirst, stickRead: true));
            Assert.True(InputManager.IsSourceSuppressedPostpone(Slot, onFirst, stickRead: false));
        }

        /// <summary>End to end on the stick lane: an angle source rests while
        /// an activator holds its second axis, and steers with "Also Fire
        /// Activator's Own Mapping" on.</summary>
        [Fact]
        public void AnAngleSourceRestsWhileAnActivatorHoldsItsSecondAxis()
        {
            var a = Device(A);
            a.InputState.Axis[0] = 65535;
            a.InputState.Axis[1] = 65535;
            MappingSource Steer() => new() { Kind = "AngleToAxisX", DeviceGuid = A, Descriptor = "Axis 0", ParamYDescriptor = "Axis 1" };

            short held = Frame(SteeringRow(Steer(), activatorOn: "Axis 1"), 1.0, a).ThumbLX;
            InputManager.ClearAllShiftRuntime();
            InputManager.ClearSourceKindRuntime();
            short fired = Frame(SteeringRow(Steer(), activatorOn: "Axis 1", alsoFire: true), 1.0, a).ThumbLX;

            Assert.Equal(0, held);
            Assert.NotEqual(0, fired);
        }

        /// <summary>One row on controller A's hat or one of its directions,
        /// and a Hold activator on the hat's Up.</summary>
        private static MappingSet HatRow(string target, string descriptor, bool alsoFire = false,
            string activatorOn = "POV 0 Up")
        {
            var set = new MappingSet();
            set.Rows.Add(new MappingRow
            {
                Target = target, LayerMask = "Base", CombineMode = "",
                Sources = new[] { Direct(descriptor) }.ToList(),
            });
            set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = A, Descriptor = activatorOn, LayerMask = "Held", LayerName = "Held",
                Mode = "Hold", Kind = "Button", PostponeMapping = alsoFire, InheritUnmapped = true,
            });
            SettingsManager.SlotMappingSets[Slot] = set;
            return set;
        }

        /// <summary>An activator on one direction of a hat holds that
        /// direction back from the combined D-pad row, as it holds it back
        /// from the row of that direction alone. The combined row's own
        /// input is the whole hat, "POV 0", which no direction's name
        /// equals, so the row was weighed as a reader of another input and
        /// pressed the direction a layer's activator held. The directions
        /// the activator does not hold read on, and with "Also Fire
        /// Activator's Own Mapping" on the held one fires too.</summary>
        [Fact]
        public void TheCombinedDPadRow_HoldsBackTheDirectionAnActivatorHolds()
        {
            var a = Device(A);

            a.InputState.Povs[0] = 0;   // up
            Assert.Equal(0, Frame(HatRow("DPad", "POV 0"), 1.0, a).Buttons & Gamepad.DPAD_UP);
            // The row of the direction alone, the answer the combined row follows.
            Assert.Equal(0, Frame(HatRow("DPadUp", "POV 0 Up"), 1.0, a).Buttons & Gamepad.DPAD_UP);

            a.InputState.Povs[0] = 4500;   // up and right
            var diagonal = Frame(HatRow("DPad", "POV 0"), 1.0, a);
            Assert.Equal(0, diagonal.Buttons & Gamepad.DPAD_UP);
            Assert.NotEqual(0, diagonal.Buttons & Gamepad.DPAD_RIGHT);

            a.InputState.Povs[0] = 9000;   // right alone: the activator is not held
            Assert.NotEqual(0, Frame(HatRow("DPad", "POV 0"), 1.0, a).Buttons & Gamepad.DPAD_RIGHT);

            a.InputState.Povs[0] = 0;
            Assert.NotEqual(0, Frame(HatRow("DPad", "POV 0", alsoFire: true), 1.0, a).Buttons & Gamepad.DPAD_UP);
        }

        /// <summary>Each direction is held back by an activator on it, and
        /// by no activator on another direction.</summary>
        [Theory]
        [InlineData("Up", 0, Gamepad.DPAD_UP)]
        [InlineData("Right", 9000, Gamepad.DPAD_RIGHT)]
        [InlineData("Down", 18000, Gamepad.DPAD_DOWN)]
        [InlineData("Left", 27000, Gamepad.DPAD_LEFT)]
        public void TheCombinedDPadRow_HoldsBackEachDirectionByItsOwnActivator(string direction, int pov, ushort bit)
        {
            var a = Device(A);
            a.InputState.Povs[0] = pov;
            Assert.Equal(0, Frame(HatRow("DPad", "POV 0", activatorOn: "POV 0 " + direction), 1.0, a).Buttons & bit);

            // An activator on the opposite direction is not held, and holds
            // nothing back.
            string opposite = direction switch { "Up" => "Down", "Down" => "Up", "Left" => "Right", _ => "Left" };
            Assert.NotEqual(0, Frame(HatRow("DPad", "POV 0", activatorOn: "POV 0 " + opposite), 1.0, a).Buttons & bit);
        }

        // ── The mouse lanes pick a source by its Descriptor ─────────────

        private static T Invoke<T>(string method, params object[] args)
        {
            var m = typeof(InputManager).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            return (T)m.Invoke(null, args);
        }

        /// <summary>The gyro rate lane turns the cursor from a gyro source.
        /// A key source that kept "Gyro Yaw" turned it too.</summary>
        [Fact]
        public void TheGyroMouseLane_ReadsNoKeySourcesKeptGyro()
        {
            var a = Device(A);
            a.InputState.Gyro[0] = a.InputState.Gyro[1] = a.InputState.Gyro[2] = 3f;
            FrameDeltas[Slot] = 0.004;
            var (keyX, _) = Invoke<(float, float)>("TickGyroMouseSources",
                a.InputState, Row("KbmMouseX", KeySource("Incremental", kept: "Gyro Yaw")), A, Slot);
            var (gyroX, _) = Invoke<(float, float)>("TickGyroMouseSources",
                a.InputState, Row("KbmMouseX", Direct("Gyro Yaw")), A, Slot);
            Assert.Equal(0f, keyX);
            Assert.NotEqual(0f, gyroX);
        }

        private static IDictionary TouchBalls => (IDictionary)typeof(SourceCoercion)
            .GetField("_touchVelocity", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

        /// <summary>The trackpad rate lane starts tracking a finger for a
        /// touch source. A key source that kept a touch position started it
        /// too.</summary>
        [Fact]
        public void TheTrackpadMouseLane_ReadsNoKeySourcesKeptTouch()
        {
            var a = Device(A);
            var pad = new TouchpadInputState(2);
            pad.FingerDown[0] = true;
            pad.FingerX[0] = pad.FingerY[0] = 0.5f;
            a.InputState.Touchpads = new[] { pad };
            FrameDeltas[Slot] = 0.004;
            var ball = (Slot, A, 0, 0);

            Invoke<(float, float)>("TickTouchpadMouseSources",
                a.InputState, Row("KbmMouseX", KeySource("Incremental", kept: "Touchpad 0 Finger 0 X")), A, Slot);
            Assert.False(TouchBalls.Contains(ball));
            Invoke<(float, float)>("TickTouchpadMouseSources",
                a.InputState, Row("KbmMouseX", Direct("Touchpad 0 Finger 0 X")), A, Slot);
            Assert.True(TouchBalls.Contains(ball));
        }

        /// <summary>The flick lane ticks a flick source. A key source that
        /// kept "Flick Stick Right" ticked one.</summary>
        [Fact]
        public void TheFlickLane_ReadsNoKeySourcesKeptFlickStick()
        {
            var a = Device(A);
            var flickState = typeof(SourceKindRuntime).GetField("_flickState", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(flickState);
            int FlickStates() => ((ICollection)flickState.GetValue(InputManager.GetSlotSourceKindRuntime(Slot))).Count;
            FrameDeltas[Slot] = 0.004;

            Invoke<int>("TickFlickStickSources",
                a.InputState, Row("KbmMouseX", KeySource("Incremental", kept: SourceCoercion.FlickStickRightDescriptor)), A, Slot);
            Assert.Equal(0, FlickStates());
            Invoke<int>("TickFlickStickSources",
                a.InputState, Row("KbmMouseX", Direct(SourceCoercion.FlickStickRightDescriptor)), A, Slot);
            Assert.Equal(1, FlickStates());
        }

        // ── The mouse rate lanes turn the way the row says ──────────────

        private static MappingSource InvertOnHold(int button)
            => new() { Kind = "InvertOnHold", ParamModifier = Button(button), ParamModifierDeviceGuid = A };

        private static void NextFrame() => StickFrame.SetValue(null, (long)StickFrame.GetValue(null) + 1);

        private static readonly FieldInfo StickFrame = typeof(InputManager)
            .GetField("_stickTrimFrameSeq", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>A left flick: rest, push right, then the first tick of the
        /// turn, the counts the lane sends.</summary>
        private static int FlickRight(UserDevice a, MappingSet set)
        {
            InputManager.ClearSourceKindRuntime();
            a.InputState.Axis[0] = 32768;
            Invoke<int>("TickFlickStickSources", a.InputState, set, A, Slot);
            NextFrame();
            a.InputState.Axis[0] = 65535;
            Invoke<int>("TickFlickStickSources", a.InputState, set, A, Slot);
            NextFrame();
            return Invoke<int>("TickFlickStickSources", a.InputState, set, A, Slot);
        }

        /// <summary>A finger dragged right across touchpad 0, the counts the
        /// lane sends for the second report.</summary>
        private static float DragRight(UserDevice a, MappingSet set)
        {
            TouchBalls.Remove((Slot, A, 0, 0));
            var pad = new TouchpadInputState(2);
            pad.FingerDown[0] = true;
            pad.FingerX[0] = pad.FingerY[0] = 0.5f;
            a.InputState.Touchpads = new[] { pad };
            SourceCoercion.BeginPollFrame();
            Invoke<(float, float)>("TickTouchpadMouseSources", a.InputState, set, A, Slot);
            System.Threading.Thread.Sleep(5);
            pad.FingerX[0] = 0.6f;
            SourceCoercion.BeginPollFrame();
            return Invoke<(float, float)>("TickTouchpadMouseSources", a.InputState, set, A, Slot).Item1;
        }

        /// <summary>A flick source's Invert turns the flick the other way, as
        /// the bipolar evaluator turns every other source.</summary>
        [Fact]
        public void AnInvertedFlickSource_TurnsTheOtherWay()
        {
            var a = Device(A);
            FrameDeltas[Slot] = 0.004;
            int plain = FlickRight(a, Row("KbmMouseX", Direct(SourceCoercion.FlickStickLeftDescriptor)));
            int inverted = FlickRight(a, Row("KbmMouseX", Direct(SourceCoercion.FlickStickLeftDescriptor, invert: true)));
            Assert.True(plain > 0, "plain = " + plain);
            Assert.Equal(-plain, inverted);
        }

        /// <summary>An Invert on Hold modifier flips the whole row, so the
        /// gyro lane's part of a mouse row flips with it.</summary>
        [Fact]
        public void InvertOnHold_TurnsTheGyroMouseLane()
        {
            var a = Device(A);
            a.InputState.Gyro[1] = 3f;
            FrameDeltas[Slot] = 0.004;
            var set = Row("KbmMouseX", Direct("Gyro Yaw"), InvertOnHold(Free));
            float released = Invoke<(float, float)>("TickGyroMouseSources", a.InputState, set, A, Slot).Item1;
            Hold(a, Free);
            float held = Invoke<(float, float)>("TickGyroMouseSources", a.InputState, set, A, Slot).Item1;
            Assert.NotEqual(0f, released);
            Assert.Equal(-released, held, 4);
        }

        [Fact]
        public void InvertOnHold_TurnsTheTouchpadMouseLane()
        {
            var a = Device(A);
            FrameDeltas[Slot] = 0.004;
            var set = Row("KbmMouseX", Direct("Touchpad 0 Finger 0 X"), InvertOnHold(Free));
            float released = DragRight(a, set);
            Hold(a, Free);
            float held = DragRight(a, set);
            Assert.True(released > 0f, "released = " + released);
            Assert.True(held < 0f, "held = " + held);
        }

        [Fact]
        public void InvertOnHold_TurnsTheFlickLane()
        {
            var a = Device(A);
            FrameDeltas[Slot] = 0.004;
            var set = Row("KbmMouseX", Direct(SourceCoercion.FlickStickLeftDescriptor), InvertOnHold(Free));
            int released = FlickRight(a, set);
            Hold(a, Free);
            int held = FlickRight(a, set);
            Assert.True(released > 0, "released = " + released);
            Assert.Equal(-released, held);
        }

        /// <summary>The absolute cursor lanes take the row's pointer source.
        /// A key source that kept a pointer took the cursor.</summary>
        [Fact]
        public void ThePointerLanes_TakeNoKeySourcesKeptPointer()
        {
            var a = Device(A);
            a.InputState.Ir.Detected = true;
            var pad = new TouchpadInputState(2);
            pad.FingerDown[0] = true;
            pad.FingerX[0] = pad.FingerY[0] = 0.5f;
            a.InputState.Touchpads = new[] { pad };

            Assert.Null(Invoke<MappingSource>("FindIrPointerSource",
                a.InputState, Row("KbmMouseX", KeySource("Incremental", kept: "IR Pointer X")), "KbmMouseX", null, A, Slot));
            Assert.NotNull(Invoke<MappingSource>("FindIrPointerSource",
                a.InputState, Row("KbmMouseX", Direct("IR Pointer X")), "KbmMouseX", null, A, Slot));

            Assert.Null(Invoke<MappingSource>("FindEngagedTouchpadPointerSource",
                a.InputState, Row("KbmMouseX", KeySource("Incremental", kept: "Touchpad 0 Pointer X")), "KbmMouseX", Slot, A));
            Assert.NotNull(Invoke<MappingSource>("FindEngagedTouchpadPointerSource",
                a.InputState, Row("KbmMouseX", Direct("Touchpad 0 Pointer X")), "KbmMouseX", Slot, A));
        }

        /// <summary>The Sticks tab's preview of a stick the mouse drives
        /// shows the row's first cursor source. A key source that kept
        /// "Mouse Position X" came first and showed the stick centered.</summary>
        [Fact]
        public void TheMouseStickPreview_SkipsAKeySourcesKeptCursor()
        {
            var value = typeof(InputService).GetMethod("MouseCursorStickValue", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(value);
            SourceCoercion.MouseCursorProvider = () => (0.9f, 0.5f);
            var key = KeySource("Incremental", kept: "Mouse Position X");
            key.DeviceGuid = "";
            var set = Row("LeftThumbAxisX", key, Direct("Mouse Position X", device: ""));
            short shown = (short)value.Invoke(null, new object[] { set, "LeftThumbAxisX", A, new CustomInputState(), Slot });
            short alone = (short)value.Invoke(null, new object[] { Row("LeftThumbAxisX", Direct("Mouse Position X", device: "")),
                "LeftThumbAxisX", A, new CustomInputState(), Slot });
            Assert.NotEqual(0, alone);
            Assert.Equal(alone, shown);
        }

        // ── An Up or Down key on an activator's own input ───────────────

        public static TheoryData<string, bool, string> KeysOnTheActivatorsInput()
        {
            var data = new TheoryData<string, bool, string>();
            foreach (string kind in new[] { "Incremental", "Ramped" })
                foreach (bool alsoFire in new[] { false, true })
                    foreach (string where in new[] { "in hand", "any controller", "another controller", "any-device activator", "down key" })
                        data.Add(kind, alsoFire, where);
            return data;
        }

        /// <summary>With "Also Fire Activator's Own Mapping" off, an Up or
        /// Down key on the activator's input reads released while the
        /// activator holds it, as a Direct source on that input and an Invert
        /// on Hold modifier on it do. With the option on, the key fires, as
        /// they do. The key reads its input wherever it names: on the
        /// source's own controller (the state in hand), on any controller of
        /// the slot, on another controller, held by an activator with no
        /// device of its own. The activator's own pass runs first in each
        /// frame, so the frame its press lands in is held back too: an
        /// activator is advanced on its own device's pass, and a reader on an
        /// earlier pass of that frame still sees the last frame's.</summary>
        [Theory]
        [MemberData(nameof(KeysOnTheActivatorsInput))]
        public void AKeyOnAnActivatorsInputIsHeldBackWithItsOtherReaders(string kind, bool alsoFire, string where)
        {
            var a = Device(A);
            var b = Device(B);
            var key = KeySource(kind, kept: "", up: Kept);
            string activatorDevice = A;
            string target = "LeftTrigger";
            switch (where)
            {
                case "any controller":
                    key.DeviceGuid = "";
                    key.ParamUpDeviceGuid = "";
                    break;
                case "another controller":
                    key.ParamUpDeviceGuid = B;
                    activatorDevice = B;
                    break;
                case "any-device activator":
                    activatorDevice = "";
                    break;
                case "down key":
                    key.ParamDown = key.ParamUp;
                    key.ParamDownDeviceGuid = key.ParamUpDeviceGuid;
                    key.ParamUp = "";
                    key.ParamMin = -1;
                    target = "LeftThumbAxisX";
                    break;
            }
            var set = new MappingSet();
            set.Rows.Add(new MappingRow { Target = target, LayerMask = "Base", Sources = { key } });
            set.ShiftActivators.Add(Activator(Kept, activatorDevice, alsoFire));
            SettingsManager.SlotMappingSets[Slot] = set;
            var holder = activatorDevice == B ? b : a;
            var other = holder == a ? b : a;
            Hold(holder, Kept);

            for (int frame = 0; frame < 2; frame++)
            {
                var gp = Frame(set, 1.0, holder, other);
                int moved = target == "LeftTrigger" ? gp.LeftTrigger : Math.Abs((int)gp.ThumbLX);
                if (alsoFire) Assert.True(moved > 30000, $"frame {frame}: the key did not fire ({moved})");
                else Assert.True(moved == 0, $"frame {frame}: the key fired while the activator held it ({moved})");
            }

            // The other two readers of that input, under the same option.
            int expected = alsoFire ? ushort.MaxValue : 0;
            string input = activatorDevice == B ? B : A;
            InputManager.ClearAllShiftRuntime();
            var direct = Row("LeftTrigger", Direct(Button(Kept), device: input));
            direct.ShiftActivators[0] = Activator(Kept, activatorDevice, alsoFire);
            Assert.Equal(expected, (int)Frame(direct, 1.0, holder, other).LeftTrigger);
            InputManager.ClearAllShiftRuntime();
            var modifier = Row("LeftTrigger", Direct(Button(Free)),
                new MappingSource { Kind = "InvertOnHold", DeviceGuid = A, ParamModifier = Button(Kept), ParamModifierDeviceGuid = input });
            modifier.ShiftActivators[0] = Activator(Kept, activatorDevice, alsoFire);
            Assert.Equal(expected, (int)Frame(modifier, 1.0, holder, other).LeftTrigger);
        }

        /// <summary>The app wires the hold-back of a key beside the key read
        /// when it starts, and clears both when it stops.</summary>
        [Fact]
        public void TheAppWiresTheHoldBackOfAKey()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string svc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(dir.FullName, "PadForge.App", "Services", "InputService.cs")).Replace("\r\n", "\n");
            Assert.Contains("SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;\n"
                + "            PadForge.Engine.Common.Mapping.SourceCoercion.KeyHeldBackProvider = InputManager.KeyHeldBack;", svc);
            Assert.Contains("SourceCoercion.KeyHeldProvider = null;\n"
                + "                PadForge.Engine.Common.Mapping.SourceCoercion.KeyHeldBackProvider = null;", svc);
        }

        /// <summary>An "(Any Device)" key spans the slot, and an activator
        /// pinned to A holds back A's input alone: B's own press of the same
        /// button still steps the value.</summary>
        [Fact]
        public void AnAnyControllerKeyStillReadsAnotherControllersPress()
        {
            var a = Device(A);
            var b = Device(B);
            var key = KeySource("Incremental", kept: "", up: Kept);
            key.DeviceGuid = "";
            key.ParamUpDeviceGuid = "";
            var set = Row("LeftTrigger", key);
            Hold(a, Kept);
            Assert.Equal(0, Frame(set, 1.0, a, b).LeftTrigger);
            Hold(b, Kept);
            Assert.Equal(ushort.MaxValue, Frame(set, 1.0, a, b).LeftTrigger);
        }

        /// <summary>A trigger a consuming macro eats is held back from the
        /// rows by the same test, and an Up key on it reads released while
        /// the macro holds it, as a Direct source on it does.</summary>
        [Fact]
        public void AKeyOnAnInputAConsumingMacroEatsIsHeldBack()
        {
            var a = Device(A);
            var im = new InputManager();
            var macro = new MacroItem
            {
                Name = "Eats", IsEnabled = true, PadIndex = Slot, TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once, ConsumeTriggerButtons = true,
                TriggerInputs = "in:" + A + ":btn:" + Kept,
            };
            macro.Actions.Add(new MacroAction { Type = MacroActionType.ButtonPress, ButtonFlags = Gamepad.B });
            try
            {
                var key = KeySource("Incremental", kept: "", up: Kept);
                var set = new MappingSet();
                set.Rows.Add(new MappingRow { Target = "LeftTrigger", LayerMask = "Base", Sources = { key } });
                SettingsManager.SlotMappingSets[Slot] = set;
                Hold(a, Kept);
                im.MacroSnapshots[Slot] = new[] { macro };
                im.RebuildConsumedTriggerSources();
                Assert.True(InputManager.IsSourceSuppressedPostpone(Slot, A, Button(Kept)));
                Assert.Equal(0, Frame(set, 1.0, a).LeftTrigger);

                // The macro gone, the key steps.
                im.MacroSnapshots[Slot] = null;
                im.RebuildConsumedTriggerSources();
                Assert.Equal(ushort.MaxValue, Frame(set, 1.0, a).LeftTrigger);
            }
            finally
            {
                im.MacroSnapshots[Slot] = null;
                im.RebuildConsumedTriggerSources();
            }
        }
    }
}
