using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Output held by its cause (#600): key and mouse output a program fed is
    /// held from a window that runs with rights, and the person's own goes
    /// out. The windows are stood in through the gate's seams, every event is
    /// read through the send seam, and the test host sends nothing
    /// (DesktopInputOffLimits).
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class OutputCauseTests : IDisposable
    {
        private static readonly IntPtr Medium = (IntPtr)11, High = (IntPtr)22;
        private const ushort F13 = 0x7C, F14 = 0x7D, F15 = 0x7E, F16 = 0x7F, F17 = 0x80, F18 = 0x81;
        private const uint KeyUp = 0x0002;

        private readonly List<InputManager.InjectedEvent> _sent = new();
        private IntPtr _front = High;

        public OutputCauseTests()
        {
            InputManager.FlushPendingInput();
            ProgramInputGate.InForceSeam = () => true;
            ProgramInputGate.ForegroundSeam = () => _front;
            ProgramInputGate.PointerWindowSeam = () => Medium;
            ProgramInputGate.ProcessOfSeam = w => (uint)w;
            ProgramInputGate.IntegritySeam = pid => pid == (uint)High ? 0x3000 : 0x2000;
            ProgramInputGate.ForgetForTest();
            InputManager.SentForTest = batch => { lock (_sent) _sent.AddRange(batch); };
        }

        public void Dispose()
        {
            InputManager.FlushPendingInput();
            InputManager.SentForTest = null;
            ProgramInputGate.InForceSeam = null;
            ProgramInputGate.ForegroundSeam = null;
            ProgramInputGate.PointerWindowSeam = null;
            ProgramInputGate.ProcessOfSeam = null;
            ProgramInputGate.IntegritySeam = null;
            ProgramInputGate.ProgramNameSeam = null;
            ProgramInputGate.ForgetForTest();
            ControlHolds.Publish(null);
            ScriptValues.ResetForTest();
            ShiftRuntimes.SetValue(null, 0);
        }

        /// <summary>The keys of the given codes that went out, each as
        /// "down" or "up", in order.</summary>
        private List<string> Sent(params ushort[] vks)
        {
            InputManager.FlushPendingInput();
            lock (_sent)
                return _sent.Where(e => e.Keyboard && vks.Contains(e.Vk))
                    .Select(e => $"{e.Vk:X2} {((e.Flags & KeyUp) != 0 ? "up" : "down")}").ToList();
        }

        private void ClearSent()
        {
            InputManager.FlushPendingInput();
            lock (_sent) _sent.Clear();
        }

        // ── The queue and the Keyboard + Mouse controller ──

        [Fact]
        public void AProgramsKey_IntoAWindowWithRights_IsHeld_AndTheOwnKeyGoesOut()
        {
            var vc = new KeyboardMouseVirtualController(0);
            vc.Connect();
            try
            {
                var raw = new KbmRawState();
                raw.SetKey((byte)F13, true);
                raw.SetKey((byte)F14, true);
                var cause = KbmCause.AllOwn;
                cause.MarkKey(F14, OutputCause.Program(5));
                vc.SubmitKbmState(raw, cause);
                Assert.Equal(new[] { "7C down" }, Sent(F13, F14));
                vc.SubmitKbmState(default, KbmCause.AllOwn);
                Assert.Equal(new[] { "7C down", "7C up", "7D up" }, Sent(F13, F14));
                Assert.Equal((uint)High, ProgramInputGate.LastHeldProcess);

                // The same key a program fed goes out to a window at medium.
                _front = Medium;
                ClearSent();
                vc.SubmitKbmState(raw, cause);
                Assert.Equal(new[] { "7C down", "7D down" }, Sent(F13, F14));
                vc.SubmitKbmState(default, KbmCause.AllOwn);
            }
            finally
            {
                vc.Dispose();
            }
        }

        // ── Device and row marks ──

        private static readonly Guid Pad = new("11111111-0000-0000-0000-000000000001");
        private static readonly Guid Script = new("22222222-0000-0000-0000-000000000002");

        private static UserDevice OwnPad() => new() { InstanceGuid = Pad };

        private static UserDevice ProgramPad(int session)
        {
            var device = new ProgramDevice(Script, Guid.NewGuid(), "program://test", "Script pad", new ProgramDeviceShape())
            {
                OwnerSession = session,
            };
            return new UserDevice { InstanceGuid = Script, Device = device };
        }

        private static CustomInputState State(params int[] buttonsDown)
        {
            var s = new CustomInputState();
            foreach (int b in buttonsDown) s.Buttons[b] = true;
            return s;
        }

        private static MappingRow Row(string target, string device, string descriptor)
            => new() { Target = target, Sources = { new MappingSource { DeviceGuid = device, Descriptor = descriptor } } };

        private static string Key(ushort vk) => $"KbmKey{vk:X2}";

        [Fact]
        public void TheOwnKeyMapping_GoesOut_WhileAProgramDrivesPadForge_AndARowFedByBothIsHeld()
        {
            var im = new InputManager();
            var set = new MappingSet();
            set.Rows.Add(Row(Key(F13), Pad.ToString(), "Button 0"));       // the person's pad alone
            set.Rows.Add(Row(Key(F14), "", "Button 0"));                    // any device of the slot
            set.Rows.Add(Row(Key(F15), Script.ToString(), "Button 0"));    // the program's pad
            im.NoteProgramMarks(OwnPad(), State(0));
            im.NoteProgramMarks(ProgramPad(8), State(0));
            im.WorkOutSlotCauses(0, set, new[] { Pad, Script });
            var causes = im.CombinedKbmCauses[0];
            Assert.True(causes.Key(F13).IsOwn);
            Assert.Equal(8, causes.Key(F14).Session);
            Assert.Equal(8, causes.Key(F15).Session);
            Assert.Equal(8, im.SlotMacroCauses[0].Session);
        }

        [Fact]
        public void AHeldControl_MarksOnlyItsRows_AndKeepsTheMarkUntilTheDevicesOwnValueChanges()
        {
            var im = new InputManager();
            var set = new MappingSet();
            set.Rows.Add(Row(Key(F13), Pad.ToString(), "Button 0"));
            set.Rows.Add(Row(Key(F14), Pad.ToString(), "Button 3"));
            ControlHolds.Publish(new Dictionary<Guid, ControlHolds.DeviceHolds>
            {
                [Pad] = new ControlHolds.DeviceHolds(new[] { (3, true) }, null, null) { Owner = 9 },
            });
            im.NoteProgramMarks(OwnPad(), State());
            im.WorkOutSlotCauses(0, set, new[] { Pad });
            Assert.True(im.CombinedKbmCauses[0].Key(F13).IsOwn);
            Assert.Equal(9, im.CombinedKbmCauses[0].Key(F14).Session);

            // The program lets go: button 3 stays marked while the pad's own
            // button 3 stays as it was, and loses the mark once it moves.
            ControlHolds.Publish(null);
            NextPass(im, State(), set);
            Assert.Equal(9, im.CombinedKbmCauses[0].Key(F14).Session);
            NextPass(im, State(3), set);
            Assert.True(im.CombinedKbmCauses[0].Key(F14).IsOwn);
        }

        /// <summary>A device a program feeds costs the poll thread no
        /// allocation per pass: the devices of a slot come as a list, and
        /// "(Any Device)" rows read every one of them.</summary>
        [Fact]
        public void WorkingOutASlotsCauses_AllocatesNothing_WhileAProgramFeedsADevice()
        {
            var im = new InputManager();
            var set = new MappingSet();
            for (int i = 0; i < 4; i++) set.Rows.Add(Row(Key((ushort)(F13 + i)), "", "Button 0"));
            var devices = new List<Guid> { Pad, Script };
            im.NoteProgramMarks(OwnPad(), State());
            im.NoteProgramMarks(ProgramPad(8), State(0));
            for (int i = 0; i < 2000; i++) im.WorkOutSlotCauses(0, set, devices);
            Thread.Sleep(300);
            for (int i = 0; i < 2000; i++) im.WorkOutSlotCauses(0, set, devices);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) im.WorkOutSlotCauses(0, set, devices);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(8, im.CombinedKbmCauses[0].Key(F13).Session);
            Assert.Equal(0, allocated);
        }

        private static void NextPass(InputManager im, CustomInputState own, MappingSet set)
        {
            BeginMarks.Invoke(im, null);
            im.NoteProgramMarks(OwnPad(), own);
            im.WorkOutSlotCauses(0, set, new[] { Pad });
        }

        private static readonly MethodInfo BeginMarks = typeof(InputManager)
            .GetMethod("BeginProgramMarks", BindingFlags.NonPublic | BindingFlags.Instance);

        // ── Macros ──

        internal static MacroItem Macro(MacroTriggerMode mode, params MacroAction[] actions)
        {
            var m = new MacroItem
            {
                Name = "Cause",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerMode = mode,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
            };
            foreach (var a in actions) m.Actions.Add(a);
            return m;
        }

        internal static void Pass(InputManager im, MacroItem[] macros, bool pressed, OutputCause slot)
        {
            im.SlotMacroCauses[0] = slot;
            im.BeginLatchWants();
            var gp = new Gamepad { Buttons = pressed ? Gamepad.A : (ushort)0 };
            im.EvaluateSlotMacros(ref gp, macros);
            im.ReconcileLatchedKeys();
        }

        [Fact]
        public void AMacroAProgramStarted_KeepsItsMarkThroughItsDelayedSteps()
        {
            var im = new InputManager();
            var macros = new[]
            {
                Macro(MacroTriggerMode.OnPress,
                    new MacroAction { Type = MacroActionType.Delay, DurationMs = 30 },
                    new MacroAction { Type = MacroActionType.KeyPress, KeyCode = F15, DurationMs = 0 }),
            };
            Pass(im, macros, pressed: true, OutputCause.Program(4));
            Pass(im, macros, pressed: false, OutputCause.Own);
            Thread.Sleep(60);
            Pass(im, macros, pressed: false, OutputCause.Own);
            Pass(im, macros, pressed: false, OutputCause.Own);
            Assert.Equal(new[] { "7E up" }, Sent(F15));

            // The run is over. The person's own press starts a run of their own.
            Pass(im, macros, pressed: false, OutputCause.Own);
            Assert.False(macros[0].IsExecuting);
            ClearSent();
            Pass(im, macros, pressed: true, OutputCause.Own);
            Thread.Sleep(60);
            Pass(im, macros, pressed: false, OutputCause.Own);
            Pass(im, macros, pressed: false, OutputCause.Own);
            Assert.Equal(new[] { "7E down", "7E up" }, Sent(F15));
        }

        [Fact]
        public void AToggleAndItsTurbo_AProgramSwitched_KeepTheMark_UntilThePersonSetsThemAgain()
        {
            var im = new InputManager();
            var macros = new[]
            {
                Macro(MacroTriggerMode.OnPress,
                    new MacroAction { Type = MacroActionType.ToggleKey, KeyCode = F16, LatchDirection = MacroLatchDirection.Toggle, PulseWhileLatched = true }),
            };
            Pass(im, macros, pressed: true, OutputCause.Program(4));      // the program switches it on
            Pass(im, macros, pressed: false, OutputCause.Own);
            for (int i = 0; i < 40; i++)
            {
                Pass(im, macros, pressed: false, OutputCause.Own);         // its turbo pulses on alone
                Thread.Sleep(5);
            }
            Assert.DoesNotContain("7F down", Sent(F16));

            Pass(im, macros, pressed: true, OutputCause.Own);              // the person switches it off
            Pass(im, macros, pressed: false, OutputCause.Own);
            ClearSent();
            Pass(im, macros, pressed: true, OutputCause.Own);              // and on again
            Assert.Contains("7F down", Sent(F16));
            Pass(im, macros, pressed: false, OutputCause.Own);
            Pass(im, macros, pressed: true, OutputCause.Own);
            Pass(im, macros, pressed: false, OutputCause.Own);
        }

        // ── Shift layers ──

        private static readonly FieldInfo ShiftRuntimesField = typeof(InputManager)
            .GetField("_shiftRuntime", BindingFlags.NonPublic | BindingFlags.Static);

        private static Array ShiftRuntimes => (Array)ShiftRuntimesField.GetValue(null);

        [Fact]
        public void ALayerAProgramSwitched_KeepsTheMark_UntilThePersonsInputSetsItAgain()
        {
            var im = new InputManager();
            var set = new MappingSet();
            set.Rows.Add(Row(Key(F18), Pad.ToString(), "Button 0"));
            set.ShiftActivators.Add(new ShiftActivator { DeviceGuid = Script.ToString(), Descriptor = "Button 1", Mode = "Toggle" });
            var rtType = typeof(InputManager).GetNestedType("ShiftRuntime", BindingFlags.NonPublic);
            object rt = Activator.CreateInstance(rtType, true);
            rtType.GetMethod("EnsureSize").Invoke(rt, new object[] { 1 });
            var toggleOn = (bool[])rtType.GetField("ToggleOn").GetValue(rt);
            ShiftRuntimes.SetValue(rt, 0);
            var both = new[] { Pad, Script };

            // The program's press toggles the layer on.
            toggleOn[0] = true;
            im.NoteProgramMarks(OwnPad(), State(0));
            im.NoteProgramMarks(ProgramPad(3), State(1));
            im.WorkOutSlotCauses(0, set, both);
            Assert.Equal(3, im.CombinedKbmCauses[0].Key(F18).Session);

            // The program is gone, the layer stays on and keeps the mark.
            BeginMarks.Invoke(im, null);
            im.NoteProgramMarks(OwnPad(), State(0));
            im.WorkOutSlotCauses(0, set, both);
            Assert.Equal(3, im.CombinedKbmCauses[0].Key(F18).Session);

            // The person's own input toggles it off, and on again.
            toggleOn[0] = false;
            im.WorkOutSlotCauses(0, set, both);
            toggleOn[0] = true;
            im.WorkOutSlotCauses(0, set, both);
            Assert.True(im.CombinedKbmCauses[0].Key(F18).IsOwn);
        }
            // ── The notices ──

        [Fact]
        public void Status_SaysTheGateHolds_OnlyWhileAProgramDrivesPadForge_AndNamesTheWindowItHeldFrom()
        {
            bool inForce = false;
            ProgramInputGate.InForceSeam = () => inForce;
            ProgramInputGate.ProgramNameSeam = pid => pid == (uint)High ? "Registry Editor" : null;
            Assert.Null(PadForge.Services.Automation.AssistantTools.HeldBackWords());
            inForce = true;
            Assert.Equal(PadForge.Services.Automation.AssistantTools.GateNotice,
                PadForge.Services.Automation.AssistantTools.HeldBackWords());
            InputManager.GateForTest(OutputCause.Program(2), new InputManager.InjectedEvent(true, F13, 0, 0, 0, 0, 0));
            Assert.EndsWith("Output was held from Registry Editor, which runs as administrator and is in front now.",
                PadForge.Services.Automation.AssistantTools.HeldBackWords());
            _front = Medium;
            InputManager.GateForTest(OutputCause.Program(2), new InputManager.InjectedEvent(true, F13, 0, 0, 0, 0, 0));
            Assert.Equal(PadForge.Services.Automation.AssistantTools.GateNotice,
                PadForge.Services.Automation.AssistantTools.HeldBackWords());
        }

        [Fact]
        public void TheFirstHold_OfASession_IsToldOnce()
        {
            var told = new List<(int, uint)>();
            Action<int, uint> listen = (s, p) => told.Add((s, p));
            ProgramInputGate.FirstHeld += listen;
            try
            {
                var key = new InputManager.InjectedEvent(true, F13, 0, 0, 0, 0, 0);
                InputManager.GateForTest(OutputCause.Program(12), key);
                InputManager.GateForTest(OutputCause.Program(12), key);
                InputManager.GateForTest(OutputCause.Program(13), key);
                InputManager.GateForTest(OutputCause.Own, key);
                Assert.Equal(new[] { (12, (uint)High), (13, (uint)High) }, told);
            }
            finally
            {
                ProgramInputGate.FirstHeld -= listen;
            }
        }

        [Fact]
        public void Activity_WordsTheHold_AndItsResult()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var ask = new PadForge.Services.Automation.AutomationAsk(PadForge.Services.Automation.AutomationAskKind.Held, "Registry Editor");
            Assert.Equal("Key and mouse output it feeds is held back from Registry Editor, which runs as administrator", ask.Text);
            Assert.Equal(s.Automation_Asked_Held, new PadForge.Services.Automation.AutomationAsk(PadForge.Services.Automation.AutomationAskKind.Held).Text);
            Assert.Equal("Held", AutomationActivityItem.RefusalWords("err output-held"));
        }

        // ── The paddle line ──

        [Fact]
        public void ThePaddleLine_ShowsSdlsOwnReason_OnlyWhileTheRouteIsOff()
        {
            Assert.Null(SdlDeviceWrapper.PaddleReasonOf(true, "anything"));
            Assert.Null(SdlDeviceWrapper.PaddleReasonOf(false, null));
            Assert.Null(SdlDeviceWrapper.PaddleReasonOf(false, "  "));
            const string reason = "XInput identity Open stayed busy after 8 Open attempts.";
            Assert.Equal(reason, SdlDeviceWrapper.PaddleReasonOf(false, reason + " "));
            Assert.Equal("The back paddles are off over USB and the Xbox Wireless Adapter. SDL’s reason: " + reason,
                DeviceRowViewModel.PaddleNoteOf(reason, bluetooth: false));
            Assert.Equal("The back paddles are off over Bluetooth. SDL’s reason: " + reason,
                DeviceRowViewModel.PaddleNoteOf(reason, bluetooth: true));
            Assert.Null(DeviceRowViewModel.PaddleNoteOf(null, bluetooth: false));
            Assert.Equal("SDL.joystick.xinput.paddle.data_available", SdlDeviceWrapper.PaddleDataAvailableProperty);
            Assert.Equal("SDL.joystick.xinput.paddle.error", SdlDeviceWrapper.PaddleErrorProperty);
        }
            [Fact]
        public void ARunAProgramFedPartway_KeepsTheMarkThroughItsLaterSteps()
        {
            var im = new InputManager();
            var macros = new[]
            {
                Macro(MacroTriggerMode.OnPress,
                    new MacroAction { Type = MacroActionType.Delay, DurationMs = 30 },
                    new MacroAction { Type = MacroActionType.KeyPress, KeyCode = F18, DurationMs = 0 }),
            };
            Pass(im, macros, pressed: true, OutputCause.Own);              // the person starts it
            Pass(im, macros, pressed: false, OutputCause.Program(5));      // a program feeds the slot partway
            Thread.Sleep(60);
            Pass(im, macros, pressed: false, OutputCause.Own);
            Pass(im, macros, pressed: false, OutputCause.Own);
            Assert.Equal(new[] { "81 up" }, Sent(F18));
        }

        [Fact]
        public void ARawSlotsMark_StaysWhileAProgramStillHoldsOneOfItsAxes()
        {
            var im = new InputManager();
            im.SlotRawHidSurface[0] = true;
            im.SlotCustomLayouts[0] = new CustomControllerLayout { Sticks = 1, Triggers = 1 };
            var raw = RawHidState.Create(3, 8, 1);
            raw.ClearToRest(im.SlotCustomLayouts[0]);
            raw.Axes[0] = 20000;                                           // a stick a program pushed
            im.CombinedRawHidStates[0] = raw;
            var set = new MappingSet();
            im.NoteProgramMarks(ProgramPad(4), State());
            im.WorkOutSlotCauses(0, set, new[] { Script });
            Assert.Equal(4, im.SlotMacroCauses[0].Session);

            // The program's device is gone from this pass, its axis is not at rest.
            BeginMarks.Invoke(im, null);
            im.WorkOutSlotCauses(0, set, new[] { Script });
            Assert.Equal(4, im.SlotMacroCauses[0].Session);

            // At rest on a later pass, the stick centered and the trigger let
            // go, the mark comes off. The pass the device left on keeps it
            // (AReleaseTheProgramsGoneMade_StartsTheMacro_AsTheProgramsRun).
            im.CombinedRawHidStates[0].Axes[0] = 0;
            BeginMarks.Invoke(im, null);
            im.WorkOutSlotCauses(0, set, new[] { Script });
            Assert.True(im.SlotMacroCauses[0].IsOwn);

            // A trigger still pulled keeps it as well.
            im.NoteProgramMarks(ProgramPad(4), State());
            im.CombinedRawHidStates[0].Axes[2] = 0;                        // half pulled
            im.WorkOutSlotCauses(0, set, new[] { Script });
            BeginMarks.Invoke(im, null);
            im.WorkOutSlotCauses(0, set, new[] { Script });
            Assert.Equal(4, im.SlotMacroCauses[0].Session);
        }

        // ── The wiring ──

        /// <summary>The pass marks each device as Step 2 reads it, works out
        /// the causes after Step 4 and in the idle pass before the
        /// connection macros, and Step 5 sends the Keyboard + Mouse slot
        /// with them.</summary>
        [Fact]
        public void ThePassMarks_WorksOutAndSendsTheCauses()
        {
            string step2 = OffLimitsSource.Read("PadForge.App/Common/Input/InputManager.Step2.UpdateInputStates.cs");
            int apply = step2.IndexOf("newState = ControlHolds.Apply(ud.InstanceGuid, newState);", StringComparison.Ordinal);
            int note = step2.IndexOf("NoteProgramMarks(ud, rawState);", StringComparison.Ordinal);
            Assert.True(apply > 0 && note > apply);
            Assert.Contains("BeginProgramMarks();", step2);
            string loop = OffLimitsSource.Read("PadForge.App/Common/Input/InputManager.cs");
            int combine = loop.IndexOf("CombineOutputStates();", StringComparison.Ordinal);
            int macros = loop.IndexOf("EvaluateMacros();", combine, StringComparison.Ordinal);
            int workedOut = loop.IndexOf("WorkOutOutputCauses();", combine, StringComparison.Ordinal);
            Assert.True(combine > 0 && workedOut > combine && workedOut < macros);
            int idle = loop.IndexOf("EvaluateConnectionMacros();", StringComparison.Ordinal);
            int idleWorkedOut = loop.LastIndexOf("WorkOutOutputCauses();", idle, StringComparison.Ordinal);
            Assert.True(idleWorkedOut > 0 && idle - idleWorkedOut < 400);
            Assert.Contains("kbmVc.SubmitKbmState(kbmOut, CombinedKbmCauses[padIndex]);",
                OffLimitsSource.Read("PadForge.App/Common/Input/InputManager.Step5.VirtualDevices.cs"));
        }

        [Fact]
        public void AFreshEngine_WorksOutEverySlotAsTheOwn_WithNothingMarked()
        {
            var im = new InputManager();
            Assert.False(im.SlotMacroCauses[0].IsOwn);
            im.WorkOutOutputCauses();
            Assert.All(im.SlotMacroCauses, c => Assert.True(c.IsOwn));
            Assert.All(im.CombinedKbmCauses, c => Assert.False(c.AnyMarked));
            Assert.False(ProgramInputGate.MarksKept);
        }

        [Fact]
        public void AConnectionMacro_OnAProgramsDevice_IsTheProgramsOnlineOrNot()
        {
            var row = ProgramPad(21);
            row.IsOnline = false;
            SettingsManager.UserDevices.Items.Add(row);
            try
            {
                var macro = Macro(MacroTriggerMode.CustomExpression);
                macro.TriggerExpression = "!a";
                macro.TriggerExpressionVariables.Add(new MacroExpressionVariable { Source = MacroTriggerSource.DeviceConnection, DeviceGuid = Script });
                var cause = (OutputCause)TriggerCause.Invoke(new InputManager(), new object[] { macro, OutputCause.Own });
                Assert.Equal(21, cause.Session);
                macro.TriggerExpressionVariables[0].DeviceGuid = Pad;
                cause = (OutputCause)TriggerCause.Invoke(new InputManager(), new object[] { macro, OutputCause.Own });
                Assert.False(cause.IsOwn);   // slot causes of a fresh engine prove nothing
            }
            finally
            {
                SettingsManager.UserDevices.Items.Remove(row);
            }
        }

        // ── A device read the slot does not own ──

        private const uint MouseMove = 0x0001, LeftDown = 0x0002;

        private int MovesSent()
        {
            InputManager.FlushPendingInput();
            lock (_sent) return _sent.Count(e => !e.Keyboard && (e.Flags & MouseMove) != 0);
        }

        private int LeftDownsSent()
        {
            InputManager.FlushPendingInput();
            lock (_sent) return _sent.Count(e => !e.Keyboard && (e.Flags & LeftDown) != 0);
        }

        /// <summary>A Mouse Move whose axis source is a device on no slot of
        /// the macro's reads it wherever it is, so a program that drives the
        /// device drives the move: it is held from a window with rights as a
        /// program's.</summary>
        [Fact]
        public void AMouseMove_ThatReadsAProgramsDeviceOffTheSlot_IsHeldAsTheProgramsOutput()
        {
            var row = ProgramPad(7);
            row.IsOnline = true;
            row.InputState = new CustomInputState();
            row.InputState.Axis[0] = 65535;                               // pushed all the way
            SettingsManager.UserDevices.Items.Add(row);
            try
            {
                var im = new InputManager();
                im.NoteProgramMarks(row, row.InputState);
                var macros = new[]
                {
                    Macro(MacroTriggerMode.Always, new MacroAction
                    {
                        Type = MacroActionType.MouseMove, AxisSource = MacroAxisSource.InputDevice,
                        SourceDeviceGuid = Script, SourceDeviceAxisIndex = 0, MouseSensitivity = 20, DurationMs = 1000,
                    }),
                };
                ClearSent();
                Pass(im, macros, pressed: false, OutputCause.Own);
                Pass(im, macros, pressed: false, OutputCause.Own);
                Assert.Equal(0, MovesSent());

                // The same move goes out to a window at medium.
                _front = Medium;
                Pass(im, macros, pressed: false, OutputCause.Own);
                Assert.True(MovesSent() > 0);
            }
            finally
            {
                SettingsManager.UserDevices.Items.Remove(row);
            }
        }

        // ── Latches the gate held ──

        private static readonly MethodInfo DesireMouse = typeof(InputManager)
            .GetMethod("DesireLatchedMouseButton", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo ReconcileMouse = typeof(InputManager)
            .GetMethod("ReconcileLatchedMouseButtons", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>A latched key and mouse button a program drives, wanted
        /// while a window with rights is in front, are pressed once that
        /// window leaves, and the reconcile never believes a press the gate
        /// held went out.</summary>
        [Fact]
        public void ALatchAProgramDrives_IsPressed_OnceTheWindowWithRightsLeaves()
        {
            var im = new InputManager();
            void Reconcile(bool wanted)
            {
                im.BeginLatchWants();
                if (wanted)
                {
                    im.DesireLatchedKey(F13, OutputCause.Program(5));
                    DesireMouse.Invoke(im, new object[] { MacroMouseButton.Left, OutputCause.Program(5) });
                }
                im.ReconcileLatchedKeys();
                ReconcileMouse.Invoke(im, null);
            }
            ClearSent();
            Reconcile(true);
            Assert.Empty(Sent(F13));
            Assert.Equal(0, LeftDownsSent());

            _front = Medium;
            Reconcile(true);
            Reconcile(true);
            Assert.Equal(new[] { "7C down" }, Sent(F13));
            Assert.Equal(1, LeftDownsSent());
            Reconcile(false);
            Assert.Equal(new[] { "7C down", "7C up" }, Sent(F13));
        }

        private static readonly FieldInfo LastAbsCursorX = typeof(KeyboardMouseVirtualController)
            .GetField("_lastAbsCursorX", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>An aim point a program drives is not taken as placed while
        /// the gate holds the cursor, so the cursor goes there once the
        /// window with rights leaves, though the aim point stays.</summary>
        [Fact]
        public void AnAimPointTheGateHeld_IsPlaced_OnceTheGateLetsIt()
        {
            Assert.True(PadForge.Services.CursorControlService.TryGetPrimarySize(out _, out _));
            var vc = new KeyboardMouseVirtualController(0);
            vc.Connect();
            try
            {
                var raw = new KbmRawState { MouseAbsValid = true, MouseAbsX = 0.25f, MouseAbsY = -0.5f };
                vc.SubmitKbmState(raw, KbmCause.All(OutputCause.Program(5)));
                Assert.Equal(int.MinValue, (int)LastAbsCursorX.GetValue(vc));
                _front = Medium;
                vc.SubmitKbmState(raw, KbmCause.All(OutputCause.Program(5)));
                Assert.NotEqual(int.MinValue, (int)LastAbsCursorX.GetValue(vc));
            }
            finally
            {
                vc.Dispose();
            }
        }

        // ── A program's device that goes ──

        /// <summary>A program presses its device's button and sends gone:
        /// the device is taken offline at once, so the pass after reads the
        /// release with no device of the slot marked. The release-edge macro
        /// it starts is the program's.</summary>
        [Fact]
        public void AReleaseTheProgramsGoneMade_StartsTheMacro_AsTheProgramsRun()
        {
            var im = new InputManager();
            var set = new MappingSet();
            var macros = new[]
            {
                Macro(MacroTriggerMode.OnRelease,
                    new MacroAction { Type = MacroActionType.KeyPress, KeyCode = F17, DurationMs = 0 }),
            };
            void Evaluate(bool pressed)
            {
                im.BeginLatchWants();
                var gp = new Gamepad { Buttons = pressed ? Gamepad.A : (ushort)0 };
                im.CombinedOutputStates[0] = gp;
                im.WorkOutSlotCauses(0, set, new[] { Script });
                im.EvaluateSlotMacros(ref gp, macros);
                im.ReconcileLatchedKeys();
            }

            // The program holds A.
            BeginMarks.Invoke(im, null);
            im.NoteProgramMarks(ProgramPad(8), State(0));
            Evaluate(pressed: true);
            Assert.Equal(8, im.SlotMacroCauses[0].Session);

            // Gone: Step 2 reads the device no more, and the slot is at rest.
            BeginMarks.Invoke(im, null);
            Evaluate(pressed: false);
            Assert.Equal(8, macros[0].RunCause.Session);

            // The pass after, the slot at rest and nothing marked, is the
            // person's own.
            BeginMarks.Invoke(im, null);
            Evaluate(pressed: false);
            Assert.True(im.SlotMacroCauses[0].IsOwn);
        }

        /// <summary>The device that left marks the pass it left on alone: the
        /// idle pass, which works the causes out again with no Step 2, finds
        /// the slot the person's own.</summary>
        [Fact]
        public void ADeviceThatLeft_MarksNoIdlePass()
        {
            var setting = new UserSetting { InstanceGuid = Script, MapTo = 0 };
            SettingsManager.UserSettings.Items.Add(setting);
            try
            {
                var im = new InputManager();
                BeginMarks.Invoke(im, null);
                im.NoteProgramMarks(ProgramPad(8), State(0));
                im.CombinedOutputStates[0] = new Gamepad { Buttons = Gamepad.A };
                im.WorkOutOutputCauses();
                Assert.Equal(8, im.SlotMacroCauses[0].Session);

                BeginMarks.Invoke(im, null);                                // gone
                im.CombinedOutputStates[0] = new Gamepad();
                im.WorkOutOutputCauses();
                Assert.Equal(8, im.SlotMacroCauses[0].Session);

                im.WorkOutOutputCauses();                                   // the idle pass
                Assert.True(im.SlotMacroCauses[0].IsOwn);
            }
            finally
            {
                SettingsManager.UserSettings.Items.Remove(setting);
            }
        }

        /// <summary>A program declares a device no slot uses and sends gone:
        /// its row goes at once, so the pass that reads the connection as 0
        /// finds no row. The <c>!a</c> macro that pass starts is the
        /// program's.</summary>
        [Fact]
        public void AConnectionMacro_StaysTheProgramsOnThePass_TheDevicesRowGoes()
        {
            var row = ProgramPad(21);
            row.IsOnline = true;
            SettingsManager.UserDevices.Items.Add(row);
            var im = new InputManager();
            var running = typeof(InputManager).GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic);
            try
            {
                int run = im.StampRun();
                running.SetValue(im, true);
                im.NoteEnumerationBegan(run, 1000);
                im.NoteDevicesEnumerated(run);
                var macro = Macro(MacroTriggerMode.CustomExpression,
                    new MacroAction { Type = MacroActionType.KeyPress, KeyCode = F17, DurationMs = 0 });
                macro.TriggerExpression = "!a";
                macro.TriggerExpressionVariables.Add(new MacroExpressionVariable { Source = MacroTriggerSource.DeviceConnection, DeviceGuid = Script });
                var macros = new[] { macro };
                Pass(im, macros, pressed: false, OutputCause.Own);
                Assert.False(macro.IsExecuting);

                SettingsManager.UserDevices.Items.Remove(row);           // ForgetRow
                Pass(im, macros, pressed: false, OutputCause.Own);
                Assert.Equal(21, macro.RunCause.Session);
            }
            finally
            {
                running.SetValue(im, false);
                SettingsManager.UserDevices.Items.Remove(row);
            }
        }

        // ── The volume flyout's keys ──

        private static readonly FieldInfo RestrictedField = typeof(InputManager)
            .GetField("_currentMacroSlotRestricted", BindingFlags.NonPublic | BindingFlags.Static);

        private const ushort VolumeUp = 0xAF, VolumeDown = 0xAE;

        /// <summary>The volume worker sends the flyout's four keys whatever
        /// slot the poll thread is on at that moment, here a slot fed by a
        /// gamepad-only peer (#138).</summary>
        [Fact]
        public void TheVolumeFlyoutKeys_GoOut_WhileThePollThreadIsOnARestrictedSlot()
        {
            _front = Medium;
            var im = new InputManager { SystemVolumeApply = (_, _) => { } };
            try
            {
                im.PostSystemVolume(0.5f, showOsd: true);
                ClearSent();
                RestrictedField.SetValue(null, true);
                im.SendVolumeOsdKeys();
                RestrictedField.SetValue(null, false);
                Assert.Equal(new[] { "AF down", "AF up", "AE down", "AE up" }, Sent(VolumeUp, VolumeDown));
            }
            finally
            {
                RestrictedField.SetValue(null, false);
            }
        }

        /// <summary>A restricted slot that posts its volume with the flyout
        /// gets no keystroke out, whatever slot the poll thread is on when
        /// the worker sends.</summary>
        [Fact]
        public void TheVolumeFlyoutKeys_OfARestrictedSlot_NeverGoOut()
        {
            _front = Medium;
            var im = new InputManager { SystemVolumeApply = (_, _) => { } };
            try
            {
                RestrictedField.SetValue(null, true);
                im.PostSystemVolume(0.5f, showOsd: true);
                RestrictedField.SetValue(null, false);
                ClearSent();
                im.SendVolumeOsdKeys();
                Assert.Empty(Sent(VolumeUp, VolumeDown));
            }
            finally
            {
                RestrictedField.SetValue(null, false);
            }
        }

        private static readonly MethodInfo TriggerCause = typeof(InputManager)
            .GetMethod("MacroTriggerCause", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>The question card's buttons refuse PadForge's own click
        /// or key while output still carries a program's mark, which can
        /// outlast every hold, device and value, as the status line reads
        /// it (#600).</summary>
        [Fact]
        public void TheCardsButtons_RefuseOwnOutput_WhileAProgramsMarkIsKept()
        {
            ProgramInputGate.InForceSeam = () => false;
            ProgramInputGate.ExtraInfoSeam = () => PadForge.Engine.Common.InputHookManager.OutputTag;
            try
            {
                ProgramInputGate.MarksKept = true;
                Assert.True(ProgramInputGate.RefusesOwnOutputNow());
                ProgramInputGate.MarksKept = false;
                Assert.False(ProgramInputGate.RefusesOwnOutputNow());
                ProgramInputGate.InForceSeam = () => true;
                Assert.True(ProgramInputGate.RefusesOwnOutputNow());
                ProgramInputGate.ExtraInfoSeam = () => IntPtr.Zero;
                Assert.False(ProgramInputGate.RefusesOwnOutputNow());
            }
            finally
            {
                ProgramInputGate.MarksKept = false;
                ProgramInputGate.ExtraInfoSeam = null;
            }
        }

        /// <summary>A session that ended leaves the gate's record of the
        /// sessions told once that their output was held, so the record
        /// does not grow with every script that ran: a later hold of that
        /// number is told again, which only an entry gone allows.</summary>
        [Fact]
        public void AnEndedSession_LeavesTheGatesRecordOfHeldSessions()
        {
            using var rig = new PadForge.Tests.AutomationPipeRig(PadForge.Services.Automation.AutomationLevel.SendInputs);
            using var client = rig.Open();
            int number = Assert.Single(rig.Host.Sessions.Snapshot()).Number;
            int told = 0;
            Action<int, uint> onFirst = (session, process) => { if (session == number) Interlocked.Increment(ref told); };
            ProgramInputGate.FirstHeld += onFirst;
            try
            {
                ProgramInputGate.NoteHeld(OutputCause.Program(number));
                ProgramInputGate.NoteHeld(OutputCause.Program(number));
                Assert.Equal(1, Volatile.Read(ref told));
                client.Dispose();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (rig.Host.Sessions.Snapshot().Count > 0)
                {
                    Assert.True(clock.ElapsedMilliseconds < 5000, "the session did not end");
                    Thread.Sleep(10);
                }
                ProgramInputGate.NoteHeld(OutputCause.Program(number));
                Assert.Equal(2, Volatile.Read(ref told));
            }
            finally
            {
                ProgramInputGate.FirstHeld -= onFirst;
            }
        }

        [Fact]
        public void Status_StillSaysTheGateHolds_WhileOutputKeepsAProgramsMark()
        {
            ProgramInputGate.InForceSeam = () => false;
            try
            {
                ProgramInputGate.MarksKept = true;
                Assert.Equal(PadForge.Services.Automation.AssistantTools.GateNotice,
                    PadForge.Services.Automation.AssistantTools.HeldBackWords());
            }
            finally
            {
                ProgramInputGate.MarksKept = false;
            }
            Assert.Null(PadForge.Services.Automation.AssistantTools.HeldBackWords());
        }
    }

    /// <summary>The program-set script values are one table for the whole
    /// process, and a host whose level drops below Send Inputs lets go of
    /// every value (AutomationSessions, the level's change). A test class
    /// that makes such a host runs beside the SettingsManagerStatics
    /// collection, so a test that sets a value and reads it on a later pass
    /// runs alone.</summary>
    [CollectionDefinition("ScriptValueCauses", DisableParallelization = true)]
    public sealed class ScriptValueCausesCollection { }

    [Collection("ScriptValueCauses")]
    public sealed class OutputCauseScriptValueTests : IDisposable
    {
        public void Dispose() => ScriptValues.ResetForTest();

        [Fact]
        public void ACustomExpression_FedByAScriptValue_RunsWithItsSettersCause()
        {
            var im = new InputManager();
            var macro = OutputCauseTests.Macro(MacroTriggerMode.CustomExpression,
                new MacroAction { Type = MacroActionType.KeyPress, KeyCode = 0x80, DurationMs = 0 });
            macro.TriggerExpression = "a > 0.5";
            macro.TriggerExpressionVariables.Add(new MacroExpressionVariable { Source = MacroTriggerSource.ScriptValue, ScriptValue = "boost" });
            var macros = new[] { macro };
            Assert.True(ScriptValues.Set("boost", 1, 6));
            OutputCauseTests.Pass(im, macros, pressed: false, OutputCause.Own);
            OutputCauseTests.Pass(im, macros, pressed: false, OutputCause.Own);
            Assert.Equal(6, macro.RunCause.Session);
        }

        /// <summary>A program sets x and ends. Its value reads 0 again, and
        /// the <c>!a</c> macro whose trigger that fall raises runs as the
        /// program's, as it would for <c>var x=0</c>.</summary>
        [Fact]
        public void AValueAProgramLeaves_StartsTheMacroOnItsFall_AsTheProgramsRun()
        {
            var im = new InputManager();
            var macro = OutputCauseTests.Macro(MacroTriggerMode.CustomExpression,
                new MacroAction { Type = MacroActionType.KeyPress, KeyCode = 0x80, DurationMs = 0 });
            macro.TriggerExpression = "!a";
            macro.TriggerExpressionVariables.Add(new MacroExpressionVariable { Source = MacroTriggerSource.ScriptValue, ScriptValue = "x" });
            var macros = new[] { macro };
            Assert.True(ScriptValues.Set("x", 1, 5));
            OutputCauseTests.Pass(im, macros, pressed: false, OutputCause.Own);
            Assert.False(macro.IsExecuting);
            ScriptValues.ReleaseOwner(5);
            OutputCauseTests.Pass(im, macros, pressed: false, OutputCause.Own);
            Assert.Equal(5, macro.RunCause.Session);
        }

        /// <summary>A name session 2 sets while session 1 lets go of it alone
        /// (Release): the picker's names end as ScriptValues.Names, with
        /// the name, whichever thread tells its change first.</summary>
        [Fact]
        public void AName_ASessionSetsAsAnotherLetsGoOfIt_StaysOffered()
        {
            var names = new List<string>();
            Action<string> added = n => { lock (names) if (!names.Contains(n)) names.Add(n); };
            Action<string> removed = n => { lock (names) names.Remove(n); };
            ScriptValues.NameAdded += added;
            ScriptValues.NameRemoved += removed;
            using var decided = new ManualResetEventSlim();
            using var go = new ManualResetEventSlim();
            int first = 1;
            try
            {
                Assert.True(ScriptValues.Set("x", 1, 1));
                ScriptValues.AfterReleaseForTest = () =>
                {
                    if (Interlocked.Exchange(ref first, 0) != 1) return;
                    decided.Set();
                    go.Wait(5000);
                };
                var letting = new Thread(() => ScriptValues.Release("x", 1)) { IsBackground = true };
                letting.Start();
                Assert.True(decided.Wait(5000));
                Assert.True(ScriptValues.Set("x", 2, 2));
                go.Set();
                Assert.True(letting.Join(5000));
                Assert.Equal(new[] { "x" }, ScriptValues.Names());
                lock (names) Assert.Equal(new[] { "x" }, names);
            }
            finally
            {
                go.Set();
                ScriptValues.AfterReleaseForTest = null;
                ScriptValues.NameAdded -= added;
                ScriptValues.NameRemoved -= removed;
            }
        }

        /// <summary>A name session 2 sets while session 1, which set it too,
        /// ends: the picker's names end as ScriptValues.Names, with the
        /// name, whichever thread tells its change first.</summary>
        [Fact]
        public void AName_ASessionSetsAsAnotherEnds_StaysOffered()
        {
            var names = new List<string>();
            Action<string> added = n => { lock (names) if (!names.Contains(n)) names.Add(n); };
            Action<string> removed = n => { lock (names) names.Remove(n); };
            ScriptValues.NameAdded += added;
            ScriptValues.NameRemoved += removed;
            using var decided = new ManualResetEventSlim();
            using var go = new ManualResetEventSlim();
            int first = 1;
            try
            {
                Assert.True(ScriptValues.Set("x", 1, 1));
                ScriptValues.AfterReleaseForTest = () =>
                {
                    if (Interlocked.Exchange(ref first, 0) != 1) return;
                    decided.Set();
                    go.Wait(5000);
                };
                var ending = new Thread(() => ScriptValues.ReleaseOwner(1)) { IsBackground = true };
                ending.Start();
                Assert.True(decided.Wait(5000));
                Assert.True(ScriptValues.Set("x", 2, 2));
                go.Set();
                Assert.True(ending.Join(5000));
                Assert.Equal(new[] { "x" }, ScriptValues.Names());
                lock (names) Assert.Equal(new[] { "x" }, names);
            }
            finally
            {
                go.Set();
                ScriptValues.AfterReleaseForTest = null;
                ScriptValues.NameAdded -= added;
                ScriptValues.NameRemoved -= removed;
            }
        }
    }
}
