using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Wave-1b macro family (issue #9): the RepeatVcButtonWhileHeld turbo,
    /// the ToggleVcButton / ToggleKey latches, the GyroRecenter action, and
    /// the HoldForMs trigger mode. Dispatch is exercised through the REAL
    /// slot evaluators on both output shapes (Gamepad and Extended raw),
    /// round-trips ride the same ActionData / MacroData DTOs the settings
    /// XML and the macro clipboard share, and the append-only enum
    /// contracts get their ordinal pins.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MacroWave1bTests
    {
        // ── Shared builders ──

        private static MacroItem GamepadTriggerMacro(MacroTriggerMode mode, MacroRepeatMode repeat, MacroAction action)
        {
            var m = new MacroItem
            {
                Name = "W1b",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerMode = mode,
                RepeatMode = repeat,
                ConsumeTriggerButtons = false,
            };
            m.Actions.Add(action);
            return m;
        }

        private static MacroItem ExtendedTriggerMacro(MacroTriggerMode mode, MacroRepeatMode repeat, MacroAction action)
        {
            var m = new MacroItem
            {
                Name = "W1bX",
                IsEnabled = true,
                PadIndex = 0,
                // Custom trigger: Extended button 0.
                TriggerCustomButtons = "00000001,00000000,00000000,00000000",
                TriggerMode = mode,
                RepeatMode = repeat,
                ConsumeTriggerButtons = false,
            };
            m.Actions.Add(action);
            return m;
        }

        private static RawHidState RawState(uint pressedWord0)
        {
            var raw = RawHidState.Create(8, 32, 1);
            raw.Buttons[0] = pressedWord0;
            return raw;
        }

        private static MacroAction RoundTrip(MacroAction a)
        {
            var m = new MacroItem { Name = "RT" };
            m.Actions.Add(a);
            var data = SettingsService.BuildMacroDataForMacro(m, 0);
            var clone = SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null);
            return Assert.Single(clone.Actions);
        }

        // ── Append-only enum pins ──

        [Fact]
        public void Wave1b_ActionTypes_AppendedAtEnumTail_WithPinnedOrdinals()
        {
            // The macro clipboard serializes MacroActionType numerically, so
            // new members MUST append at the tail and their values must
            // never move (wave 1b pinned 35..38; v15 appended 39..40; v16
            // appended 41..42; v18 appended 43..46; #237 appended 47..48;
            // #251 appended 49..51).
            var values = Enum.GetValues<MacroActionType>();
            Assert.Equal(MacroActionType.AxisScale, values[^1]);
            Assert.Equal(MacroActionType.AxisLatchRelease, values[^2]);
            Assert.Equal(MacroActionType.AxisSetLatched, values[^3]);
            Assert.Equal(49, (int)MacroActionType.AxisSetLatched);
            Assert.Equal(50, (int)MacroActionType.AxisLatchRelease);
            Assert.Equal(51, (int)MacroActionType.AxisScale);
            Assert.Equal(MacroActionType.ComboBreak, values[^4]);
            Assert.Equal(MacroActionType.AxisAdd, values[^5]);
            Assert.Equal(MacroActionType.ToggleWheel, values[^6]);
            Assert.Equal(MacroActionType.RepeatVcAxisWhileHeld, values[^7]);
            Assert.Equal(MacroActionType.ToggleVcAxis, values[^8]);
            Assert.Equal(MacroActionType.ToggleMouseButton, values[^9]);
            Assert.Equal(MacroActionType.CycleTapList, values[^10]);
            Assert.Equal(MacroActionType.MouseNudge, values[^11]);
            Assert.Equal(MacroActionType.MouseWheelTap, values[^12]);
            Assert.Equal(MacroActionType.AxisHold, values[^13]);

            Assert.Equal(35, (int)MacroActionType.RepeatVcButtonWhileHeld);
            Assert.Equal(36, (int)MacroActionType.ToggleVcButton);
            Assert.Equal(37, (int)MacroActionType.ToggleKey);
            Assert.Equal(38, (int)MacroActionType.GyroRecenter);
            Assert.Equal(39, (int)MacroActionType.AxisHold);
            Assert.Equal(40, (int)MacroActionType.MouseWheelTap);
            Assert.Equal(41, (int)MacroActionType.MouseNudge);
            Assert.Equal(42, (int)MacroActionType.CycleTapList);
            Assert.Equal(43, (int)MacroActionType.ToggleMouseButton);
            Assert.Equal(44, (int)MacroActionType.ToggleVcAxis);
            Assert.Equal(45, (int)MacroActionType.RepeatVcAxisWhileHeld);
            Assert.Equal(46, (int)MacroActionType.ToggleWheel);
            Assert.Equal(47, (int)MacroActionType.AxisAdd);
            Assert.Equal(48, (int)MacroActionType.ComboBreak);
        }

        // ── v18 latch family pins ──

        [Fact]
        public void ToggleVcAxis_GamepadPath_LatchesTheAxisAcrossRelease()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.ToggleVcAxis,
                AxisTarget = MacroAxisTarget.LeftTrigger,
                AxisValue = 32767,
            };
            var m = GamepadTriggerMacro(MacroTriggerMode.OnPress, MacroRepeatMode.Once, action);
            var macros = new[] { m };

            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);          // press: latch on
            Assert.True(gp.LeftTrigger > 0);

            gp = new Gamepad();                              // release
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(gp.LeftTrigger > 0);                 // latch persists

            gp = new Gamepad { Buttons = Gamepad.A };        // second press: unlatch
            im.EvaluateSlotMacros(ref gp, macros);
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, (int)gp.LeftTrigger);
        }

        [Fact]
        public void RepeatVcAxisWhileHeld_PulsesTheAxis_AndStopsOnRelease()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.RepeatVcAxisWhileHeld,
                AxisTarget = MacroAxisTarget.LeftTrigger,
                AxisValue = 32767,
                IntervalMs = 100,
            };
            var m = GamepadTriggerMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease, action);
            var macros = new[] { m };

            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            // First held frame: the turbo phase starts ON.
            Assert.True(gp.LeftTrigger > 0);

            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, (int)gp.LeftTrigger);
        }

        [Fact]
        public void ToggleMouseButton_And_ToggleWheel_RoundTripPayloads()
        {
            var mouse = RoundTrip(new MacroAction
            {
                Type = MacroActionType.ToggleMouseButton,
                MouseButton = MacroMouseButton.Right,
                PulseWhileLatched = true,
                IntervalMs = 150,
            });
            Assert.Equal(MacroActionType.ToggleMouseButton, mouse.Type);
            Assert.Equal(MacroMouseButton.Right, mouse.MouseButton);
            Assert.True(mouse.PulseWhileLatched);
            Assert.Equal(150, mouse.IntervalMs);

            var wheel = RoundTrip(new MacroAction
            {
                Type = MacroActionType.ToggleWheel,
                AxisValue = -1,
                WheelHorizontal = true,
                IntervalMs = 80,
            });
            Assert.Equal(MacroActionType.ToggleWheel, wheel.Type);
            Assert.Equal(-1, wheel.AxisValue);
            Assert.True(wheel.WheelHorizontal);
            Assert.Equal(80, wheel.IntervalMs);
        }

        [Fact]
        public void HoldForMs_TriggerMode_AppendedAtEnumTail_WithPinnedOrdinal()
        {
            // MacroData.TriggerMode rides the same numeric clipboard JSON
            // (wave 1b appended HoldForMs = 5; v17 appended DoublePress = 6;
            // #238 appended TriplePress = 7, SinglePress = 8, then
            // Toggle = 9 and Turbo = 10).
            var values = Enum.GetValues<MacroTriggerMode>();
            Assert.Equal(MacroTriggerMode.Turbo, values[^1]);
            Assert.Equal(MacroTriggerMode.Toggle, values[^2]);
            Assert.Equal(MacroTriggerMode.SinglePress, values[^3]);
            Assert.Equal(MacroTriggerMode.TriplePress, values[^4]);
            Assert.Equal(MacroTriggerMode.DoublePress, values[^5]);
            Assert.Equal(MacroTriggerMode.HoldForMs, values[^6]);
            Assert.Equal(10, (int)MacroTriggerMode.Turbo);
            Assert.Equal(9, (int)MacroTriggerMode.Toggle);
            Assert.Equal(8, (int)MacroTriggerMode.SinglePress);
            Assert.Equal(5, (int)MacroTriggerMode.HoldForMs);
            Assert.Equal(6, (int)MacroTriggerMode.DoublePress);
            Assert.Equal(7, (int)MacroTriggerMode.TriplePress);
        }

        // ── DTO round-trips (settings XML + clipboard share these) ──

        [Fact]
        public void RepeatVcButtonWhileHeld_RoundTripsTargetAndInterval()
        {
            var clone = RoundTrip(new MacroAction
            {
                Type = MacroActionType.RepeatVcButtonWhileHeld,
                ButtonFlags = Gamepad.B,
                CustomButtons = "00000004,00000000,00000000,00000000",
                IntervalMs = 250,
            });
            Assert.Equal(MacroActionType.RepeatVcButtonWhileHeld, clone.Type);
            Assert.Equal(Gamepad.B, clone.ButtonFlags);
            Assert.Equal("00000004,00000000,00000000,00000000", clone.CustomButtons);
            Assert.Equal(250, clone.IntervalMs);
        }

        [Fact]
        public void ToggleVcButton_RoundTripsTarget()
        {
            var clone = RoundTrip(new MacroAction
            {
                Type = MacroActionType.ToggleVcButton,
                ButtonFlags = Gamepad.B,
                CustomButtons = "00000002,00000000,00000000,00000000",
            });
            Assert.Equal(MacroActionType.ToggleVcButton, clone.Type);
            Assert.Equal(Gamepad.B, clone.ButtonFlags);
            Assert.Equal("00000002,00000000,00000000,00000000", clone.CustomButtons);
            // Runtime latch state never rides the DTO.
            Assert.False(clone.VcToggleLatched);
        }

        [Fact]
        public void ToggleKey_RoundTripsKey()
        {
            var clone = RoundTrip(new MacroAction
            {
                Type = MacroActionType.ToggleKey,
                KeyCode = 0x41, // VK_A
            });
            Assert.Equal(MacroActionType.ToggleKey, clone.Type);
            Assert.Equal(0x41, clone.KeyCode);
            Assert.False(clone.KeyToggleLatched);
        }

        [Fact]
        public void GyroRecenter_RoundTripsType()
        {
            var clone = RoundTrip(new MacroAction { Type = MacroActionType.GyroRecenter });
            Assert.Equal(MacroActionType.GyroRecenter, clone.Type);
        }

        [Fact]
        public void TriggerHoldMs_RoundTripsOnMacroData_AndClamps()
        {
            var m = new MacroItem
            {
                Name = "Hold",
                TriggerMode = MacroTriggerMode.HoldForMs,
                TriggerHoldMs = 750,
            };
            var data = SettingsService.BuildMacroDataForMacro(m, 0);
            Assert.Equal(750, data.TriggerHoldMs);
            var clone = SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null);
            Assert.Equal(MacroTriggerMode.HoldForMs, clone.TriggerMode);
            Assert.Equal(750, clone.TriggerHoldMs);

            // VM clamp + default.
            Assert.Equal(500, new MacroItem().TriggerHoldMs);
            Assert.Equal(50, new MacroItem { TriggerHoldMs = 1 }.TriggerHoldMs);
            Assert.Equal(10000, new MacroItem { TriggerHoldMs = 99999 }.TriggerHoldMs);

            // Pre-wave-1b MacroData (no element in the XML) hydrates the same
            // default a fresh macro gets.
            Assert.Equal(500, new MacroData().TriggerHoldMs);
        }

        // ── Turbo phase unit timing (injected timestamps) ──

        [Fact]
        public void TurboPhase_FlipsEveryHalfInterval_AndStartsOn()
        {
            var a = new MacroAction { Type = MacroActionType.RepeatVcButtonWhileHeld, IntervalMs = 100 };

            // MinValue seed: the first tick flips the phase ON immediately.
            Assert.True(InputManager.TickRepeatVcButtonPhase(a));

            // Within the half-period: no flip, still ON.
            Assert.True(InputManager.TickRepeatVcButtonPhase(a));

            // Past the half-period (injected clock): flips OFF.
            a.RepeatVcLastToggleUtc = DateTime.UtcNow.AddMilliseconds(-60);
            Assert.False(InputManager.TickRepeatVcButtonPhase(a));

            // And back ON one half-period later.
            a.RepeatVcLastToggleUtc = DateTime.UtcNow.AddMilliseconds(-60);
            Assert.True(InputManager.TickRepeatVcButtonPhase(a));
        }

        // ── Dispatch: Gamepad path ──

        [Fact]
        public void Turbo_GamepadPath_PulsesTargetWhileHeld_AndStopsOnRelease()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.RepeatVcButtonWhileHeld,
                ButtonFlags = Gamepad.B,
                IntervalMs = 100,
            };
            var macros = new[] { GamepadTriggerMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease, action) };

            // Held frame 1: phase starts ON, target ORs into the output.
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(Gamepad.B, (ushort)(gp.Buttons & Gamepad.B));

            // Half-interval later (injected clock): OFF phase, no write.
            action.RepeatVcLastToggleUtc = DateTime.UtcNow.AddMilliseconds(-60);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, gp.Buttons & Gamepad.B);

            // Another half-interval: ON again.
            action.RepeatVcLastToggleUtc = DateTime.UtcNow.AddMilliseconds(-60);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(Gamepad.B, (ushort)(gp.Buttons & Gamepad.B));

            // Release: the macro stops, nothing is written.
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, gp.Buttons);
            Assert.False(macros[0].IsExecuting);
        }

        [Fact]
        public void ToggleVcButton_GamepadPath_LatchesAcrossRelease_AndUnlatchesOnSecondPress()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.ToggleVcButton,
                ButtonFlags = Gamepad.B,
            };
            var macros = new[] { GamepadTriggerMacro(MacroTriggerMode.OnPress, MacroRepeatMode.Once, action) };

            // Press 1: latch engages and the button is written the same frame.
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(action.VcToggleLatched);
            Assert.Equal(Gamepad.B, (ushort)(gp.Buttons & Gamepad.B));

            // Released: the latch keeps the button down with the macro idle.
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(macros[0].IsExecuting);
            Assert.Equal(Gamepad.B, (ushort)(gp.Buttons & Gamepad.B));

            // Press 2: unlatches, the button releases the same frame.
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(action.VcToggleLatched);
            Assert.Equal(0, gp.Buttons & Gamepad.B);
        }

        [Fact]
        public void ToggleVcButton_DisablingMacro_ClearsLatch_AndStopsWrites()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.ToggleVcButton,
                ButtonFlags = Gamepad.B,
            };
            var macro = GamepadTriggerMacro(MacroTriggerMode.OnPress, MacroRepeatMode.Once, action);
            var macros = new[] { macro };

            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(action.VcToggleLatched);

            macro.IsEnabled = false;
            Assert.False(action.VcToggleLatched); // cleared by the disable

            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, gp.Buttons);
        }

        [Fact]
        public void ToggleKey_GamepadPath_ContributesToDesiredSet_WhileLatched()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.ToggleKey,
                KeyCode = 0x41, // VK_A
            };
            var macros = new[] { GamepadTriggerMacro(MacroTriggerMode.OnPress, MacroRepeatMode.Once, action) };

            // Press 1: latch on, key desired. (The evaluator only collects;
            // the SendInput reconcile runs in the top-level EvaluateMacros,
            // which this test deliberately does not drive.)
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(action.KeyToggleLatched);
            Assert.Contains((ushort)0x41, im._desiredLatchedKeys);

            // Released, next frame: still desired (per-frame rebuild).
            im._desiredLatchedKeys.Clear();
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Contains((ushort)0x41, im._desiredLatchedKeys);

            // Press 2: unlatched, key no longer desired.
            im._desiredLatchedKeys.Clear();
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(action.KeyToggleLatched);
            Assert.Empty(im._desiredLatchedKeys);
        }

        [Fact]
        public void GyroRecenter_GamepadPath_InvokesSlotHook_OncePerPress()
        {
            var im = new InputManager();
            int calls = 0, lastSlot = -1;
            var old = InputManager.GyroRecenterApply;
            try
            {
                InputManager.GyroRecenterApply = s => { calls++; lastSlot = s; };
                var macros = new[]
                {
                    GamepadTriggerMacro(MacroTriggerMode.OnPress, MacroRepeatMode.Once,
                        new MacroAction { Type = MacroActionType.GyroRecenter })
                };
                // High slot: the action also resets the ENGINE's slot-scoped
                // gyro caches, and the frame-gate tests run in parallel on
                // slots 0/1.
                macros[0].PadIndex = 13;

                var gp = new Gamepad { Buttons = Gamepad.A };
                im.EvaluateSlotMacros(ref gp, macros);
                Assert.Equal(1, calls);
                Assert.Equal(13, lastSlot);

                // Held frame: OnPress fires once, no repeat.
                gp = new Gamepad { Buttons = Gamepad.A };
                im.EvaluateSlotMacros(ref gp, macros);
                Assert.Equal(1, calls);
            }
            finally { InputManager.GyroRecenterApply = old; }
        }

        [Fact]
        public void HoldForMs_GamepadPath_FiresOnceAfterThreshold_TapDoesNothing()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.ToggleVcButton,
                ButtonFlags = Gamepad.B,
            };
            var macro = GamepadTriggerMacro(MacroTriggerMode.HoldForMs, MacroRepeatMode.Once, action);
            var macros = new[] { macro };

            // Short tap: press + release inside the threshold never fires.
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(action.VcToggleLatched);
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(action.VcToggleLatched);

            // Hold: arm on press, then cross the 500 ms default via the
            // injected hold-start timestamp.
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(action.VcToggleLatched);
            Assert.NotEqual(DateTime.MinValue, macro.TriggerHoldStartUtc);

            macro.TriggerHoldStartUtc = DateTime.UtcNow.AddMilliseconds(-600);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(action.VcToggleLatched); // fired

            // Still held past the threshold: no second fire.
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(action.VcToggleLatched);

            // Release re-arms; the next qualifying hold fires again.
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(action.VcToggleLatched); // armed, not yet fired
            macro.TriggerHoldStartUtc = DateTime.UtcNow.AddMilliseconds(-600);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(action.VcToggleLatched); // second fire unlatched
        }

        // ── DoublePress trigger mode (translator v17) ──

        [Fact]
        public void DoublePress_GamepadPath_FiresOnDouble_NotOnSingle()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.ToggleVcButton,
                ButtonFlags = Gamepad.B,
            };
            var macro = GamepadTriggerMacro(MacroTriggerMode.DoublePress, MacroRepeatMode.Once, action);
            var macros = new[] { macro };

            // Single press, held frames, release: never fires.
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(action.VcToggleLatched);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(action.VcToggleLatched);
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(action.VcToggleLatched);

            // Second press within the window (evaluations above ran well
            // inside the 442 ms default): fires on the rising edge.
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(action.VcToggleLatched);

            // Held frames after the fire: no repeat.
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(action.VcToggleLatched);
        }

        [Fact]
        public void DoublePress_GamepadPath_SlowSecondPress_OnlyRearms()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.ToggleVcButton,
                ButtonFlags = Gamepad.B,
            };
            var macro = GamepadTriggerMacro(MacroTriggerMode.DoublePress, MacroRepeatMode.Once, action);
            var macros = new[] { macro };

            // First press + release, then age the armed press past the
            // window via the injected timestamp.
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            macro.TriggerLastPressUtc = DateTime.UtcNow.AddMilliseconds(-600);

            // Slow second press: no fire, but it re-arms as a fresh first
            // press.
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(action.VcToggleLatched);
            Assert.NotEqual(DateTime.MinValue, macro.TriggerLastPressUtc);

            // A quick third press completes the re-armed pair and fires.
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(action.VcToggleLatched);
        }

        [Fact]
        public void DoublePress_GamepadPath_FireConsumesThePair()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.ToggleVcButton,
                ButtonFlags = Gamepad.B,
            };
            var macro = GamepadTriggerMacro(MacroTriggerMode.DoublePress, MacroRepeatMode.Once, action);
            var macros = new[] { macro };

            void Tap()
            {
                var down = new Gamepad { Buttons = Gamepad.A };
                im.EvaluateSlotMacros(ref down, macros);
                var up = new Gamepad();
                im.EvaluateSlotMacros(ref up, macros);
            }

            Tap();
            Tap(); // fires on the second press
            Assert.True(action.VcToggleLatched);
            // The pair was consumed: the third press only arms again...
            Tap();
            Assert.True(action.VcToggleLatched);
            // ...and the fourth press fires the second double.
            Tap();
            Assert.False(action.VcToggleLatched);
        }

        [Fact]
        public void DoublePress_ExtendedPath_FiresOnDouble()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.ToggleVcButton,
                CustomButtons = "00000002,00000000,00000000,00000000",
            };
            var macro = ExtendedTriggerMacro(MacroTriggerMode.DoublePress, MacroRepeatMode.Once, action);
            var macros = new[] { macro };

            var raw = RawState(0x1);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.False(action.VcToggleLatched);
            raw = RawState(0);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            raw = RawState(0x1);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.True(action.VcToggleLatched);
        }

        [Fact]
        public void TriggerDoublePressMs_RoundTripsOnMacroData_AndClamps()
        {
            var m = new MacroItem
            {
                Name = "Dbl",
                TriggerMode = MacroTriggerMode.DoublePress,
                TriggerDoublePressMs = 300,
            };
            var data = SettingsService.BuildMacroDataForMacro(m, 0);
            Assert.Equal(300, data.TriggerDoublePressMs);
            var clone = SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null);
            Assert.Equal(MacroTriggerMode.DoublePress, clone.TriggerMode);
            Assert.Equal(300, clone.TriggerDoublePressMs);

            // VM clamp + default (442, Valve's own controller_base value).
            Assert.Equal(442, new MacroItem().TriggerDoublePressMs);
            Assert.Equal(50, new MacroItem { TriggerDoublePressMs = 1 }.TriggerDoublePressMs);
            Assert.Equal(5000, new MacroItem { TriggerDoublePressMs = 99999 }.TriggerDoublePressMs);

            // A pre-v17 MacroData (no element in the XML) hydrates the same
            // default a fresh macro gets.
            Assert.Equal(442, new MacroData().TriggerDoublePressMs);
        }

        // ── Dispatch: Extended raw path ──

        [Fact]
        public void Turbo_ExtendedPath_PulsesWideButtonWhileHeld()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.RepeatVcButtonWhileHeld,
                CustomButtons = "00000002,00000000,00000000,00000000", // Extended button 1
                IntervalMs = 100,
            };
            var macros = new[] { ExtendedTriggerMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease, action) };

            var raw = RawState(0x1); // trigger: Extended button 0 held
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.Equal(0x2u, raw.Buttons[0] & 0x2u);

            action.RepeatVcLastToggleUtc = DateTime.UtcNow.AddMilliseconds(-60);
            raw = RawState(0x1);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.Equal(0u, raw.Buttons[0] & 0x2u); // OFF phase

            // Release: stops, no write.
            raw = RawState(0);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.Equal(0u, raw.Buttons[0]);
            Assert.False(macros[0].IsExecuting);
        }

        [Fact]
        public void ToggleVcButton_ExtendedPath_LatchesAcrossRelease()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.ToggleVcButton,
                CustomButtons = "00000002,00000000,00000000,00000000",
            };
            var macros = new[] { ExtendedTriggerMacro(MacroTriggerMode.OnPress, MacroRepeatMode.Once, action) };

            var raw = RawState(0x1);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.True(action.VcToggleLatched);
            Assert.Equal(0x2u, raw.Buttons[0] & 0x2u);

            raw = RawState(0);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.Equal(0x2u, raw.Buttons[0] & 0x2u); // latched with macro idle

            raw = RawState(0x1);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.False(action.VcToggleLatched);
            Assert.Equal(0u, raw.Buttons[0] & 0x2u);
        }

        [Fact]
        public void ToggleKey_ExtendedPath_ContributesToDesiredSet()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.ToggleKey,
                KeyCode = 0x42, // VK_B
            };
            var macros = new[] { ExtendedTriggerMacro(MacroTriggerMode.OnPress, MacroRepeatMode.Once, action) };

            var raw = RawState(0x1);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.True(action.KeyToggleLatched);
            Assert.Contains((ushort)0x42, im._desiredLatchedKeys);
        }

        [Fact]
        public void GyroRecenter_ExtendedPath_InvokesSlotHook()
        {
            var im = new InputManager();
            int calls = 0;
            var old = InputManager.GyroRecenterApply;
            try
            {
                InputManager.GyroRecenterApply = _ => calls++;
                var macros = new[]
                {
                    ExtendedTriggerMacro(MacroTriggerMode.OnPress, MacroRepeatMode.Once,
                        new MacroAction { Type = MacroActionType.GyroRecenter })
                };
                // High slot, same isolation rationale as the Gamepad-path test.
                macros[0].PadIndex = 14;
                var raw = RawState(0x1);
                im.EvaluateSlotMacrosExtended(ref raw, macros);
                Assert.Equal(1, calls);
            }
            finally { InputManager.GyroRecenterApply = old; }
        }

        [Fact]
        public void HoldForMs_ExtendedPath_FiresAfterThreshold()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.ToggleVcButton,
                CustomButtons = "00000002,00000000,00000000,00000000",
            };
            var macro = ExtendedTriggerMacro(MacroTriggerMode.HoldForMs, MacroRepeatMode.Once, action);
            var macros = new[] { macro };

            var raw = RawState(0x1);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.False(action.VcToggleLatched); // armed, below threshold

            macro.TriggerHoldStartUtc = DateTime.UtcNow.AddMilliseconds(-600);
            raw = RawState(0x1);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.True(action.VcToggleLatched); // fired once past threshold
        }

        // ── GyroRecenter engine-side resets ──

        [Fact]
        public void ResetGyroAimStateForSlot_ClearsEmaForThatSlotOnly()
        {
            // The reset clears EVERY device's state for the slot, so this
            // test uses slot numbers no other test touches (the frame-gate
            // tests run in parallel on slots 0 and 1).
            const string dev = "wave1b-recenter-dev";
            const float alpha = 0.5f;
            const int slot = 21, slotDecoy = 211;

            // Seed two EMA states: slot 21 and slot 211 (the parse-the-tail
            // guard: a "|21" suffix match would wrongly clear slot 211 too).
            SourceCoercion.BeginPollFrame();
            Assert.Equal(5f, SourceCoercion.ApplyGyroSmoothing(dev, slot, 0, 10f, alpha), 3);
            Assert.Equal(5f, SourceCoercion.ApplyGyroSmoothing(dev, slotDecoy, 0, 10f, alpha), 3);
            SourceCoercion.BeginPollFrame();
            Assert.Equal(7.5f, SourceCoercion.ApplyGyroSmoothing(dev, slot, 0, 10f, alpha), 3);
            Assert.Equal(7.5f, SourceCoercion.ApplyGyroSmoothing(dev, slotDecoy, 0, 10f, alpha), 3);

            SourceCoercion.ResetGyroAimStateForSlot(slot);

            // Slot 21 restarts from a fresh state; slot 211 keeps converging.
            SourceCoercion.BeginPollFrame();
            Assert.Equal(5f, SourceCoercion.ApplyGyroSmoothing(dev, slot, 0, 10f, alpha), 3);
            Assert.Equal(8.75f, SourceCoercion.ApplyGyroSmoothing(dev, slotDecoy, 0, 10f, alpha), 3);
        }

        // ── ToggleMouseButton pulse-while-latched (audit 2026-07-17 M3) ──

        [Fact]
        public void ToggleMouseButton_PulseWhileLatched_ReleasesOnTheOffPhase()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.ToggleMouseButton,
                MouseButton = MacroMouseButton.Right,
                PulseWhileLatched = true,
                IntervalMs = 100,
            };
            var macros = new[] { GamepadTriggerMacro(MacroTriggerMode.OnPress, MacroRepeatMode.Once, action) };

            // Press: latch engages, phase arms ON, button enters the desired set.
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(action.MouseToggleLatched);
            Assert.Contains(MacroMouseButton.Right, im._desiredLatchedMouseButtons);

            // Half-interval later (injected clock): the OFF half must drop
            // the button so the reconcile releases it. This branch used to
            // ignore PulseWhileLatched and hold solid.
            action.RepeatVcLastToggleUtc = DateTime.UtcNow.AddMilliseconds(-60);
            im._desiredLatchedMouseButtons.Clear();
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.DoesNotContain(MacroMouseButton.Right, im._desiredLatchedMouseButtons);

            // Same latch WITHOUT the pulse flag: solid hold, the plain
            // latch contract (same-window control).
            action.PulseWhileLatched = false;
            im._desiredLatchedMouseButtons.Clear();
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Contains(MacroMouseButton.Right, im._desiredLatchedMouseButtons);
        }

        [Fact]
        public void ToggleMouseButton_PulseWhileLatched_ExtendedTwin()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.ToggleMouseButton,
                MouseButton = MacroMouseButton.Middle,
                PulseWhileLatched = true,
                IntervalMs = 100,
            };
            var macros = new[] { ExtendedTriggerMacro(MacroTriggerMode.OnPress, MacroRepeatMode.Once, action) };

            var raw = RawState(0x1);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.True(action.MouseToggleLatched);
            Assert.Contains(MacroMouseButton.Middle, im._desiredLatchedMouseButtons);

            action.RepeatVcLastToggleUtc = DateTime.UtcNow.AddMilliseconds(-60);
            im._desiredLatchedMouseButtons.Clear();
            raw = RawState(0);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.DoesNotContain(MacroMouseButton.Middle, im._desiredLatchedMouseButtons);
        }

        // ── Press legs use the fired-latch, not the sub-1ms window
        //    (audit 2026-07-17 M5) ──

        [Fact]
        public void DelayThenKeyPress_LoadedFrame_StillSendsTheDown()
        {
            var im = new InputManager();
            // VK_NONAME (0xFC, "reserved, no effect"): the Down/Up really
            // route through SendInput but carry an inert key, so the test
            // never types into the host. Legacy KeyCode path parses it
            // without the VirtualKey vocabulary.
            var press = new MacroAction
            {
                Type = MacroActionType.KeyPress,
                KeyCode = 0xFC,
                DurationMs = 400,
            };
            var macro = GamepadTriggerMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.Once,
                new MacroAction { Type = MacroActionType.Delay, DurationMs = 1 });
            macro.Actions.Add(press);
            var macros = new[] { macro };

            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);            // Delay current
            System.Threading.Thread.Sleep(5);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);            // Delay elapses, advance
            System.Threading.Thread.Sleep(5);                 // loaded gap, way past 1 ms
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);            // press leg's first frame

            // The old actionElapsed < 1 window skipped the Down here
            // while the Up at DurationMs still fired.
            Assert.Contains(press, im._pressDownSent);

            System.Threading.Thread.Sleep(450);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);            // Up leg + advance
            Assert.DoesNotContain(press, im._pressDownSent);  // re-armed
        }

        [Fact]
        public void DelayThenMouseButtonPress_LoadedFrame_ExtendedTwin()
        {
            var im = new InputManager();
            // Out-of-range button value: SendMouseButtonInput's switch
            // default drops it, so no real click reaches the OS while the
            // press leg's fired-latch bookkeeping (the contract under
            // test) runs exactly as for a real button.
            var press = new MacroAction
            {
                Type = MacroActionType.MouseButtonPress,
                MouseButton = (MacroMouseButton)99,
                DurationMs = 400,
            };
            var macro = ExtendedTriggerMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.Once,
                new MacroAction { Type = MacroActionType.Delay, DurationMs = 1 });
            macro.Actions.Add(press);
            var macros = new[] { macro };

            var raw = RawState(0x1);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            System.Threading.Thread.Sleep(5);
            raw = RawState(0x1);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            System.Threading.Thread.Sleep(5);
            raw = RawState(0x1);
            im.EvaluateSlotMacrosExtended(ref raw, macros);

            Assert.Contains(press, im._pressDownSent);

            System.Threading.Thread.Sleep(450);
            raw = RawState(0x1);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.DoesNotContain(press, im._pressDownSent);
        }
    }

    /// <summary>GyroRecenter's MotionLean half: dropping the captured neutral
    /// re-references the CURRENT grip. Serialized with the other
    /// GravityProvider-swapping tests (shared static provider).</summary>
    [Collection("SettingsManagerStatics")]
    public class MacroWave1bMotionNeutralTests
    {
        private static readonly (float, float, float) Rest = (0f, 9.8f, 0f);
        private static readonly (float, float, float) Tilt30 = (-4.9f, 8.4870f, 0f);

        [Fact]
        public void ResetMotionNeutral_RecapturesCurrentGripAsNeutral()
        {
            var runtime = new SourceKindRuntime();
            var old = SourceCoercion.GravityProvider;
            try
            {
                var grav = Rest;
                SourceCoercion.GravityProvider = _ => grav;
                var src = new PadForge.Engine.Data.MappingSource
                {
                    Kind = "Direct",
                    Descriptor = SourceCoercion.MotionLeanDescriptor,
                    DeviceGuid = "66666666-7777-8888-9999-aaaaaaaaaaaa",
                };
                var state = new CustomInputState();

                // Neutral captured at rest; a 30-degree tilt then reads ~0.5.
                Assert.Equal(0, runtime.TickMotionLean(0, "LeftThumbAxisX", 0, src, state, src.DeviceGuid), 2);
                grav = Tilt30;
                Assert.Equal(0.5, Math.Abs(runtime.TickMotionLean(0, "LeftThumbAxisX", 0, src, state, src.DeviceGuid)), 1);

                // Recenter: the tilted grip becomes the new neutral and the
                // same physical pose now reads centered.
                runtime.ResetMotionNeutral();
                Assert.Equal(0, runtime.TickMotionLean(0, "LeftThumbAxisX", 0, src, state, src.DeviceGuid), 2);
            }
            finally { SourceCoercion.GravityProvider = old; }
        }
    }
}
