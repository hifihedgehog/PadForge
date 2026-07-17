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

        private static ExtendedRawState RawState(uint pressedWord0)
        {
            var raw = ExtendedRawState.Create(8, 32, 1);
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
            // never move (wave 1b pinned 35..38; v15 appended 39..40).
            var values = Enum.GetValues<MacroActionType>();
            Assert.Equal(MacroActionType.MouseWheelTap, values[^1]);
            Assert.Equal(MacroActionType.AxisHold, values[^2]);
            Assert.Equal(MacroActionType.GyroRecenter, values[^3]);
            Assert.Equal(MacroActionType.ToggleKey, values[^4]);
            Assert.Equal(MacroActionType.ToggleVcButton, values[^5]);
            Assert.Equal(MacroActionType.RepeatVcButtonWhileHeld, values[^6]);

            Assert.Equal(35, (int)MacroActionType.RepeatVcButtonWhileHeld);
            Assert.Equal(36, (int)MacroActionType.ToggleVcButton);
            Assert.Equal(37, (int)MacroActionType.ToggleKey);
            Assert.Equal(38, (int)MacroActionType.GyroRecenter);
            Assert.Equal(39, (int)MacroActionType.AxisHold);
            Assert.Equal(40, (int)MacroActionType.MouseWheelTap);
        }

        [Fact]
        public void HoldForMs_TriggerMode_AppendedAtEnumTail_WithPinnedOrdinal()
        {
            // MacroData.TriggerMode rides the same numeric clipboard JSON.
            var values = Enum.GetValues<MacroTriggerMode>();
            Assert.Equal(MacroTriggerMode.HoldForMs, values[^1]);
            Assert.Equal(5, (int)MacroTriggerMode.HoldForMs);
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
    }

    /// <summary>GyroRecenter's MotionLean half: dropping the captured neutral
    /// re-references the CURRENT grip. Serialized with the other
    /// GravityProvider-swapping tests (shared static provider).</summary>
    [Collection("GravityProviderSerial")]
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
