using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #237 (advanced axis macros, the reWASD combo blocks):
    /// AxisAdd (relative deflection summed with the mapped value),
    /// ComboBreak (multi-part sequences resuming on the next press), and
    /// the AxisYieldToPhysical gate on the absolute holds. Dispatch runs
    /// through the REAL slot evaluators on both output shapes, and the
    /// persistence field rides the same ActionData DTO round-trip the
    /// settings XML uses.
    /// </summary>
    public class AdvancedAxisMacroTests
    {
        private static MacroItem GamepadMacro(MacroTriggerMode mode, MacroRepeatMode repeat, params MacroAction[] actions)
        {
            var m = new MacroItem
            {
                Name = "AAM",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerMode = mode,
                RepeatMode = repeat,
                ConsumeTriggerButtons = false,
            };
            foreach (var a in actions) m.Actions.Add(a);
            return m;
        }

        private static MacroAction RoundTrip(MacroAction a)
        {
            var m = new MacroItem { Name = "RT" };
            m.Actions.Add(a);
            var data = SettingsService.BuildMacroDataForMacro(m, 0);
            var clone = SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null);
            return Assert.Single(clone.Actions);
        }

        // ── Persistence ──

        [Fact]
        public void AxisYieldToPhysical_SurvivesTheActionDataRoundTrip()
        {
            var back = RoundTrip(new MacroAction
            {
                Type = MacroActionType.AxisHold,
                AxisTarget = MacroAxisTarget.LeftTrigger,
                AxisValue = 32767,
                AxisYieldToPhysical = true,
            });
            Assert.True(back.AxisYieldToPhysical);

            var off = RoundTrip(new MacroAction
            {
                Type = MacroActionType.AxisHold,
                AxisTarget = MacroAxisTarget.LeftTrigger,
                AxisValue = 32767,
            });
            Assert.False(off.AxisYieldToPhysical);
        }

        [Fact]
        public void AxisAdd_And_ComboBreak_SurviveTheRoundTrip()
        {
            var add = RoundTrip(new MacroAction
            {
                Type = MacroActionType.AxisAdd,
                AxisTarget = MacroAxisTarget.RightStickX,
                AxisValue = -16000,
            });
            Assert.Equal(MacroActionType.AxisAdd, add.Type);
            Assert.Equal(-16000, add.AxisValue);

            var brk = RoundTrip(new MacroAction { Type = MacroActionType.ComboBreak });
            Assert.Equal(MacroActionType.ComboBreak, brk.Type);
        }

        // ── AxisAdd semantics (Gamepad path) ──

        [Fact]
        public void AxisAdd_SumsWithTheMappedValue_StickFrame()
        {
            var im = new InputManager();
            var m = GamepadMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease,
                new MacroAction
                {
                    Type = MacroActionType.AxisAdd,
                    AxisTarget = MacroAxisTarget.LeftStickX,
                    AxisValue = -16000,
                    DurationMs = 60000,
                });
            var macros = new[] { m };

            // Physical stick at +20000; the relative add lands on top.
            var gp = new Gamepad { Buttons = Gamepad.A, ThumbLX = 20000 };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(4000, gp.ThumbLX);
        }

        [Fact]
        public void AxisAdd_ClampsAtTheRangeEdges()
        {
            var im = new InputManager();
            var m = GamepadMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease,
                new MacroAction
                {
                    Type = MacroActionType.AxisAdd,
                    AxisTarget = MacroAxisTarget.LeftStickX,
                    AxisValue = -30000,
                    DurationMs = 60000,
                });
            var macros = new[] { m };

            var gp = new Gamepad { Buttons = Gamepad.A, ThumbLX = -20000 };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(short.MinValue, gp.ThumbLX);
        }

        [Fact]
        public void AxisAdd_TriggerTarget_AddsOnThePullScale()
        {
            var im = new InputManager();
            var m = GamepadMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease,
                new MacroAction
                {
                    Type = MacroActionType.AxisAdd,
                    AxisTarget = MacroAxisTarget.LeftTrigger,
                    AxisValue = 8000,   // +8000 * 2 = +16000 on the 0..65535 output
                    DurationMs = 60000,
                });
            var macros = new[] { m };

            var gp = new Gamepad { Buttons = Gamepad.A, LeftTrigger = 30000 };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(46000, gp.LeftTrigger);

            // Negative add subtracts and clamps at zero.
            m.Actions[0].AxisValue = -20000;
            gp = new Gamepad { Buttons = Gamepad.A, LeftTrigger = 30000 };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, gp.LeftTrigger);
        }

        // ── Yield-to-physical (absolute deflection, reWASD contract) ──

        [Fact]
        public void AxisHold_WithoutYield_MacroWins()
        {
            var im = new InputManager();
            var m = GamepadMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease,
                new MacroAction
                {
                    Type = MacroActionType.AxisHold,
                    AxisTarget = MacroAxisTarget.LeftTrigger,
                    AxisValue = 32767,
                    DurationMs = 60000,
                });
            var macros = new[] { m };

            var gp = new Gamepad { Buttons = Gamepad.A, LeftTrigger = 20000 };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(65535, gp.LeftTrigger);
        }

        [Fact]
        public void AxisHold_WithYield_PhysicalMovementWins_AndLatches()
        {
            var im = new InputManager();
            var m = GamepadMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease,
                new MacroAction
                {
                    Type = MacroActionType.AxisHold,
                    AxisTarget = MacroAxisTarget.LeftTrigger,
                    AxisValue = 32767,
                    AxisYieldToPhysical = true,
                    DurationMs = 60000,
                });
            var macros = new[] { m };

            // Frame 1: physical at rest, macro asserts the full pull.
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(65535, gp.LeftTrigger);

            // Frame 2: the user pulls the physical trigger past the yield
            // threshold; the macro write is suppressed, physical survives.
            gp = new Gamepad { Buttons = Gamepad.A, LeftTrigger = 20000 };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(20000, gp.LeftTrigger);

            // Frame 3: the yield is LATCHED for the activation; even with
            // the physical back at rest the macro stays yielded.
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, gp.LeftTrigger);

            // Release re-arms; the next activation asserts again.
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(65535, gp.LeftTrigger);
        }

        // ── Combo break sequencing ──

        [Fact]
        public void ComboBreak_ParksAndResumesOnTheNextPress()
        {
            var im = new InputManager();
            var m = GamepadMacro(MacroTriggerMode.OnPress, MacroRepeatMode.Once,
                new MacroAction { Type = MacroActionType.AxisSet, AxisTarget = MacroAxisTarget.LeftTrigger, AxisValue = 1000 },
                new MacroAction { Type = MacroActionType.ComboBreak },
                new MacroAction { Type = MacroActionType.AxisSet, AxisTarget = MacroAxisTarget.LeftTrigger, AxisValue = 2000 });
            var macros = new[] { m };

            // Press 1: part one runs (AxisSet advances same frame, the
            // break parks on the following frame).
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(1000, gp.LeftTrigger);

            gp = new Gamepad { Buttons = Gamepad.A };  // still held: break parks
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(m.IsExecuting);
            Assert.Equal(2, m.ComboResumeIndex);

            // Release, then press 2: resumes at part two.
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(2000, gp.LeftTrigger);

            // Completing the final part re-arms from the top.
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, m.ComboResumeIndex);

            // Release, press 3: back to part one.
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(1000, gp.LeftTrigger);
        }

        [Fact]
        public void ComboBreak_WhileHeldTrigger_NeverAutoResumesThroughTheBreak()
        {
            var im = new InputManager();
            var m = GamepadMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.Once,
                new MacroAction { Type = MacroActionType.AxisSet, AxisTarget = MacroAxisTarget.LeftTrigger, AxisValue = 1000 },
                new MacroAction { Type = MacroActionType.ComboBreak },
                new MacroAction { Type = MacroActionType.AxisSet, AxisTarget = MacroAxisTarget.LeftTrigger, AxisValue = 2000 });
            var macros = new[] { m };

            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);      // part one
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);      // break parks
            Assert.False(m.IsExecuting);
            Assert.True(m.AwaitReleaseAfterBreak);

            // Held frames must NOT restart through the break.
            for (int i = 0; i < 3; i++)
            {
                gp = new Gamepad { Buttons = Gamepad.A };
                im.EvaluateSlotMacros(ref gp, macros);
                Assert.False(m.IsExecuting);
                Assert.Equal(0, gp.LeftTrigger);
            }

            // Release opens the guard; the next hold resumes at part two.
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(m.AwaitReleaseAfterBreak);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(2000, gp.LeftTrigger);
        }

        [Fact]
        public void ComboBreak_DisablingTheMacro_ResetsThePark()
        {
            var im = new InputManager();
            var m = GamepadMacro(MacroTriggerMode.OnPress, MacroRepeatMode.Once,
                new MacroAction { Type = MacroActionType.AxisSet, AxisTarget = MacroAxisTarget.LeftTrigger, AxisValue = 1000 },
                new MacroAction { Type = MacroActionType.ComboBreak },
                new MacroAction { Type = MacroActionType.AxisSet, AxisTarget = MacroAxisTarget.LeftTrigger, AxisValue = 2000 });
            var macros = new[] { m };

            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(2, m.ComboResumeIndex);

            m.IsEnabled = false;
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, m.ComboResumeIndex);
            Assert.False(m.AwaitReleaseAfterBreak);

            // Re-enabled: a released frame re-arms the OnPress edge, then
            // the press starts from the top.
            m.IsEnabled = true;
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(1000, gp.LeftTrigger);
        }

        // ── Extended raw-path siblings ──

        [Fact]
        public void ExtendedPath_AxisAdd_And_Break_MirrorTheGamepadSemantics()
        {
            var im = new InputManager();
            var m = new MacroItem
            {
                Name = "AAMX",
                IsEnabled = true,
                PadIndex = 0,
                TriggerCustomButtons = "00000001,00000000,00000000,00000000",
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
            };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.AxisAdd,
                AxisTarget = MacroAxisTarget.LeftStickX,
                AxisValue = -16000,
                DurationMs = 0,
            });
            m.Actions.Add(new MacroAction { Type = MacroActionType.ComboBreak });
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.AxisSet,
                AxisTarget = MacroAxisTarget.LeftStickX,
                AxisValue = 5000,
            });
            var macros = new[] { m };

            var raw = RawHidState.Create(8, 32, 1);
            raw.Buttons[0] = 1;
            raw.Axes[0] = 20000;
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.Equal(4000, raw.Axes[0]);            // additive, word frame

            raw = RawHidState.Create(8, 32, 1);
            raw.Buttons[0] = 1;
            im.EvaluateSlotMacrosExtended(ref raw, macros);  // break parks
            Assert.False(m.IsExecuting);
            Assert.Equal(2, m.ComboResumeIndex);

            raw = RawHidState.Create(8, 32, 1);    // release
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            raw = RawHidState.Create(8, 32, 1);    // press 2: part two
            raw.Buttons[0] = 1;
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.Equal(5000, raw.Axes[0]);
        }
    
        // ── #251: latched ladder, release, proportional scale ──

        private static MacroItem LadderMacro()
        {
            return GamepadMacro(MacroTriggerMode.OnPress, MacroRepeatMode.Once,
                new MacroAction { Type = MacroActionType.AxisSetLatched, AxisTarget = MacroAxisTarget.LeftTrigger, AxisValue = 24575 },
                new MacroAction { Type = MacroActionType.ComboBreak },
                new MacroAction { Type = MacroActionType.AxisSetLatched, AxisTarget = MacroAxisTarget.LeftTrigger, AxisValue = 27852 },
                new MacroAction { Type = MacroActionType.ComboBreak },
                new MacroAction { Type = MacroActionType.AxisSetLatched, AxisTarget = MacroAxisTarget.LeftTrigger, AxisValue = 31128 });
        }

        private static ushort LadderTick(InputManager im, MacroItem[] macros, bool held)
        {
            var gp = new Gamepad { Buttons = held ? Gamepad.A : (ushort)0 };
            im.EvaluateSlotMacros(ref gp, macros);
            return gp.LeftTrigger;
        }

        /// <summary>Use case 1 (#251): a press-by-press ladder whose value
        /// HOLDS between presses (across combo-break parks), each press
        /// REPLACING the value, and lap 2 relatching instead of the
        /// ToggleVcAxis flip that unlatches.</summary>
        [Fact]
        public void Ladder_HoldsAndReplacesAcrossParks()
        {
            var im = new InputManager();
            var macros = new[] { LadderMacro() };

            // One sequential action executes per tick, so a real press
            // spans the latch tick AND the break tick before the release
            // (exactly as a 1 kHz poll would see it).
            LadderTick(im, macros, held: true);              // latch 75
            LadderTick(im, macros, held: true);              // break parks
            ushort held75 = LadderTick(im, macros, held: false);
            Assert.True(Math.Abs(held75 - 49150) < 700, $"expected ~75% pull, got {held75}");

            LadderTick(im, macros, held: true);              // latch 85 (replaces 75)
            LadderTick(im, macros, held: true);              // break parks
            ushort held85 = LadderTick(im, macros, held: false);
            Assert.True(Math.Abs(held85 - 55704) < 700, $"expected ~85% pull, got {held85}");

            LadderTick(im, macros, held: true);              // latch 95
            LadderTick(im, macros, held: true);              // sequence completes, re-arms
            ushort held95 = LadderTick(im, macros, held: false);
            Assert.True(Math.Abs(held95 - 62256) < 700, $"expected ~95% pull, got {held95}");

            LadderTick(im, macros, held: true);              // lap 2: latch 75 again
            LadderTick(im, macros, held: true);
            ushort lap2 = LadderTick(im, macros, held: false);
            Assert.True(Math.Abs(lap2 - 49150) < 700, $"lap 2 must relatch ~75%, got {lap2}");
        }

        /// <summary>Use case 1's nullify key (#251): a SECOND macro's
        /// Release Axis Latches clears the ladder macro's latch, returning
        /// the axis to physical control.</summary>
        [Fact]
        public void ReleaseLatches_ClearsAcrossMacros()
        {
            var im = new InputManager();
            var ladder = LadderMacro();
            var nullify = new MacroItem
            {
                Name = "Nullify",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.B,
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
            };
            nullify.Actions.Add(new MacroAction
            {
                Type = MacroActionType.AxisLatchRelease,
                AxisTarget = MacroAxisTarget.None,
            });
            var macros = new[] { ladder, nullify };

            LadderTick(im, macros, held: true);
            LadderTick(im, macros, held: true);   // break tick
            Assert.True(LadderTick(im, macros, held: false) > 40000);

            var gp = new Gamepad { Buttons = Gamepad.B };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, LadderTick(im, macros, held: false));
        }

        /// <summary>Use case 2 (#251): Scale Axis -50% halves the current
        /// deflection, +50% amplifies with a full-scale clamp.</summary>
        [Fact]
        public void ScaleAxis_HalvesAndAmplifies()
        {
            var walkDown = GamepadMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease,
                new MacroAction { Type = MacroActionType.AxisScale, AxisTarget = MacroAxisTarget.LeftStickX, AxisValue = -16384, DurationMs = 1000 });
            var im = new InputManager();
            var gp = new Gamepad { Buttons = Gamepad.A, ThumbLX = 30000 };
            im.EvaluateSlotMacros(ref gp, new[] { walkDown });
            Assert.True(Math.Abs(gp.ThumbLX - 15000) < 300, $"expected ~15000, got {gp.ThumbLX}");

            var amplify = GamepadMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease,
                new MacroAction { Type = MacroActionType.AxisScale, AxisTarget = MacroAxisTarget.LeftStickX, AxisValue = 16384, DurationMs = 1000 });
            var im2 = new InputManager();
            var gp2 = new Gamepad { Buttons = Gamepad.A, ThumbLX = 30000 };
            im2.EvaluateSlotMacros(ref gp2, new[] { amplify });
            Assert.Equal(short.MaxValue, gp2.ThumbLX);
        }

        /// <summary>The axis-family editor speaks PERCENT (owner report
        /// 2026-07-24: a bare raw -32768..32767 box gave no way to know
        /// that typing 75 meant 0.2%). The VM converts to the persisted
        /// raw short and clamps to -100..100.</summary>
        [Fact]
        public void AxisValuePercent_MapsAndClamps()
        {
            var a = new MacroAction { Type = MacroActionType.AxisSetLatched };

            a.AxisValuePercent = 75;
            Assert.True(Math.Abs(a.AxisValue - 24575) <= 1, $"75% -> {a.AxisValue}");
            Assert.Equal(75, a.AxisValuePercent);

            a.AxisValuePercent = -50;
            Assert.True(Math.Abs(a.AxisValue - (-16384)) <= 1, $"-50% -> {a.AxisValue}");

            a.AxisValuePercent = 250;      // clamped to 100
            Assert.Equal(short.MaxValue, a.AxisValue);

            a.AxisValue = 32767;           // raw write reflects back as percent
            Assert.Equal(100, a.AxisValuePercent);
        }

        /// <summary>#251 members sit at pinned tail ordinals (the clipboard
        /// serializes numerically).</summary>
        [Fact]
        public void ActionTypeEnum_251TailPinned()
        {
            Assert.Equal(49, (int)MacroActionType.AxisSetLatched);
            Assert.Equal(50, (int)MacroActionType.AxisLatchRelease);
            Assert.Equal(51, (int)MacroActionType.AxisScale);
            var values = Enum.GetValues<MacroActionType>();
            Assert.Equal(MacroActionType.AxisScale, values[^1]);
        }
    }

    /// <summary>Owner repro 2026-07-24: "Scale Axis does nothing." The
    /// exact live-config shape (OnPress + Once + consumed raw device
    /// button + AxisScale LX +50% 5000ms) driven through the REAL slot
    /// evaluator with the real device-resolution statics. Pins the three
    /// ticks the bench needs: apply on press, per-tick reapply while the
    /// state rebuilds, and survival of trigger RELEASE inside the
    /// duration window (the tap-then-move usage). Also pins the two
    /// documented no-change cases: rest (0 x 1.5 = 0) and full
    /// deflection (clamp), which is why +50%% is invisible on a fully
    /// held stick.</summary>
    [Collection("SettingsManagerStatics")]
    public class ScaleAxisUserShapeTests : IDisposable
    {
        private static readonly Guid DevGuid = new("13ea3b23-bb17-802d-f268-c194414535f8");
        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;

        public ScaleAxisUserShapeTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
        }

        [Fact]
        public void ScaleAxis_OnPressOnce_ConsumedRawButton_AppliesAndSurvivesRelease()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var state = new PadForge.Engine.CustomInputState();
            var ud = new PadForge.Engine.Data.UserDevice
            {
                InstanceGuid = DevGuid,
                ProductName = "Steam Controller",
                IsOnline = true,
                InputState = state,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(
                    new PadForge.Engine.Data.UserSetting { InstanceGuid = DevGuid, MapTo = 0 });

            var m = new MacroItem
            {
                Name = "Macro 1",
                IsEnabled = true,
                PadIndex = 0,
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = true,
                TriggerInputs = "in:13ea3b23-bb17-802d-f268-c194414535f8:btn:0",
            };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.AxisScale,
                AxisTarget = MacroAxisTarget.LeftStickX,
                AxisValue = 16384,
                DurationMs = 5000,
            });
            var macros = new[] { m };
            var im = new InputManager();

            // Tick 1: raw A down, stick partly deflected: +50% applies.
            state.Buttons[0] = true;
            var gp = new Gamepad { ThumbLX = 16000 };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(Math.Abs(gp.ThumbLX - 24000) < 300, $"tick1 held: {gp.ThumbLX}");

            // Tick 2: state rebuilds every poll; the current action re-applies.
            gp = new Gamepad { ThumbLX = 16000 };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(Math.Abs(gp.ThumbLX - 24000) < 300, $"tick2 held: {gp.ThumbLX}");

            // Tick 3: trigger RELEASED inside the 5000ms window. Once-mode
            // sequences run to completion, so the scale must still apply
            // (tap the button, then move the stick).
            state.Buttons[0] = false;
            gp = new Gamepad { ThumbLX = 16000 };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(Math.Abs(gp.ThumbLX - 24000) < 300, $"tick3 released: {gp.ThumbLX}");

            // Documented no-change cases: rest and full deflection (clamp).
            gp = new Gamepad { ThumbLX = 0 };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, gp.ThumbLX);
            gp = new Gamepad { ThumbLX = short.MaxValue };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(short.MaxValue, gp.ThumbLX);
        }
    }

    /// <summary>Owner report 2026-07-25: "Consume Trigger Buttons doesn't
    /// actually consume the button" for raw device-button triggers. The
    /// strip in Step 4b only ever covered virtual (Xbox bitmask) triggers;
    /// raw/descriptor triggers were inert from the day they shipped
    /// (ad77addb). The fix suppresses the trigger's mapping SOURCES at the
    /// Step 3 read gate while the trigger is physically active. These pins
    /// drive the real population + the real gate.</summary>
    [Collection("SettingsManagerStatics")]
    public class ConsumeRawTriggerTests : IDisposable
    {
        private static readonly Guid DevGuid = new("13ea3b23-bb17-802d-f268-c194414535f8");
        private static readonly string DevGuidStr = DevGuid.ToString();
        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;

        public ConsumeRawTriggerTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
        }

        private static PadForge.Engine.CustomInputState Arrange(out InputManager im, MacroItem macro)
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var state = new PadForge.Engine.CustomInputState();
            var ud = new PadForge.Engine.Data.UserDevice
            {
                InstanceGuid = DevGuid,
                ProductName = "Steam Controller",
                IsOnline = true,
                InputState = state,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(
                    new PadForge.Engine.Data.UserSetting { InstanceGuid = DevGuid, MapTo = 0 });
            im = new InputManager();
            im.MacroSnapshots[0] = new[] { macro };
            return state;
        }

        private static MacroItem ConsumeMacro(bool consume = true, bool enabled = true) => new()
        {
            Name = "Macro 1",
            IsEnabled = enabled,
            PadIndex = 0,
            TriggerMode = MacroTriggerMode.OnPress,
            RepeatMode = MacroRepeatMode.Once,
            ConsumeTriggerButtons = consume,
            TriggerInputs = "in:13ea3b23-bb17-802d-f268-c194414535f8:btn:0",
        };

        [Fact]
        public void ConsumedRawButton_SuppressesItsMappingSource_WhileHeld()
        {
            var state = Arrange(out var im, ConsumeMacro());

            // Held: the source reads suppressed under the concrete guid,
            // the empty-guid ("any device") row form, and the "Gamepad A"
            // alias spelling (the gate canonicalizes on lookup).
            state.Buttons[0] = true;
            im.RebuildConsumedTriggerSources();
            Assert.True(InputManager.IsSourceSuppressedPostpone(0, DevGuidStr, "Button 0"), "concrete guid");
            Assert.True(InputManager.IsSourceSuppressedPostpone(0, "", "Button 0"), "any-device row");
            Assert.False(InputManager.IsSourceSuppressedPostpone(0, DevGuidStr, "Button 1"), "same-window negative control");

            // Released: nothing suppressed.
            state.Buttons[0] = false;
            im.RebuildConsumedTriggerSources();
            Assert.False(InputManager.IsSourceSuppressedPostpone(0, DevGuidStr, "Button 0"), "released");
        }

        [Fact]
        public void ConsumeOff_OrDisabled_SuppressesNothing()
        {
            var state = Arrange(out var im, ConsumeMacro(consume: false));
            state.Buttons[0] = true;
            im.RebuildConsumedTriggerSources();
            Assert.False(InputManager.IsSourceSuppressedPostpone(0, DevGuidStr, "Button 0"), "consume off");

            var state2 = Arrange(out var im2, ConsumeMacro(enabled: false));
            state2.Buttons[0] = true;
            im2.RebuildConsumedTriggerSources();
            Assert.False(InputManager.IsSourceSuppressedPostpone(0, DevGuidStr, "Button 0"), "macro disabled");
        }

        [Fact]
        public void VirtualBitmaskTrigger_DoesNotEnterTheSourceSuppressionSet()
        {
            // The Step 4b bitmask strip still owns virtual triggers; the
            // Step 3 set must stay empty for them.
            var m = new MacroItem
            {
                Name = "V",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = true,
            };
            var state = Arrange(out var im, m);
            state.Buttons[0] = true;
            im.RebuildConsumedTriggerSources();
            Assert.False(InputManager.IsSourceSuppressedPostpone(0, DevGuidStr, "Button 0"));
        }
    }


}
