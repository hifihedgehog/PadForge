using System;
using PadForge.Common.Input;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #290 pressure-scaled turbo: the rate function and both accumulator
    /// families. The design accumulates in RATE space (a fixed clock with an
    /// analog-scaled accumulator, the DS4Windows stick-to-wheel /
    /// JoyShockMapper scroll shape) precisely because both references that
    /// divide a period by the live analog value needed repairs: antimicrox
    /// special-cases d == 0 and re-phases mid-cycle, and DS4MapperTest's
    /// unguarded DurationMs / ButtonDistance free-runs at max rate on a zero
    /// distance ((int)+inf == int.MinValue). These tests lock the no-divide
    /// contract: zero pressure is a defined slow rate, never a blowup.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class PressureTurboTests
    {
        private static MacroAction Turbo(int fastMs = 100, int slowMs = 500, string curve = "Linear")
            => new MacroAction
            {
                Type = MacroActionType.RepeatVcButtonWhileHeld,
                IntervalMs = fastMs,
                SlowIntervalMs = slowMs,
                TurboRateCurve = curve,
                PressureScaledRate = true,
            };

        // ── The rate function ──

        [Fact]
        public void Rate_ZeroPressure_IsTheSlowRate_NotZeroAndNotInfinity()
        {
            var a = Turbo(fastMs: 100, slowMs: 500);
            Assert.Equal(2.0, InputManager.PressureTurboRateHz(a, 0f), 3);
        }

        [Fact]
        public void Rate_FullPressure_IsTheFastRate()
        {
            var a = Turbo(fastMs: 100, slowMs: 500);
            Assert.Equal(10.0, InputManager.PressureTurboRateHz(a, 1f), 3);
        }

        [Fact]
        public void Rate_HalfPressure_IsLinearInRate_NotInPeriod()
        {
            // Midpoint of 2 Hz and 10 Hz is 6 Hz. A period-space interpolation
            // (300 ms period = 3.33 Hz) would land elsewhere; rate-space is
            // what makes the response perceptually linear in taps per second.
            var a = Turbo(fastMs: 100, slowMs: 500);
            Assert.Equal(6.0, InputManager.PressureTurboRateHz(a, 0.5f), 3);
        }

        [Fact]
        public void Rate_OutOfRangePressure_Clamps()
        {
            var a = Turbo(fastMs: 100, slowMs: 500);
            Assert.Equal(2.0, InputManager.PressureTurboRateHz(a, -3f), 3);
            Assert.Equal(10.0, InputManager.PressureTurboRateHz(a, 42f), 3);
        }

        [Fact]
        public void Rate_SlowBelowFast_IsFlooredAtFast_NeverInverts()
        {
            // SlowIntervalMs 50 < IntervalMs 100: the engine floors slow at
            // fast, so light press can never repeat faster than full press.
            var a = Turbo(fastMs: 100, slowMs: 50);
            Assert.Equal(10.0, InputManager.PressureTurboRateHz(a, 0f), 3);
            Assert.Equal(10.0, InputManager.PressureTurboRateHz(a, 1f), 3);
        }

        [Fact]
        public void Rate_AggressiveCurve_ShapesThePressure()
        {
            // Aggressive = x²: half press shapes to 0.25, so the rate is
            // 2 + 0.25 * 8 = 4 Hz (SourceCoercion.ApplyOutputCurve's arm).
            var a = Turbo(fastMs: 100, slowMs: 500, curve: "Aggressive");
            Assert.Equal(4.0, InputManager.PressureTurboRateHz(a, 0.5f), 3);
        }

        // ── The square-wave family ──

        [Fact]
        public void SquareWave_FreshActivation_FlipsOnImmediately()
        {
            var a = Turbo();
            Assert.True(InputManager.TickRepeatVcButtonPhase(a, 0f));
            Assert.True(a.RepeatVcPulseOn);
        }

        [Fact]
        public void SquareWave_TogglesOncePerHalfCycle_AtTheScaledRate()
        {
            // 10 Hz at full press: a half cycle is 50 ms. Injected clock:
            // first tick arms (ON), then a 50 ms step toggles OFF, then
            // another toggles back ON.
            var a = Turbo(fastMs: 100, slowMs: 500);
            Assert.True(InputManager.TickRepeatVcButtonPhase(a, 1f));

            a.TurboLastTickUtc = DateTime.UtcNow.AddMilliseconds(-50);
            Assert.False(InputManager.TickRepeatVcButtonPhase(a, 1f));

            a.TurboLastTickUtc = DateTime.UtcNow.AddMilliseconds(-50);
            Assert.True(InputManager.TickRepeatVcButtonPhase(a, 1f));
        }

        [Fact]
        public void SquareWave_ZeroPressure_StillTogglesAtTheSlowRate()
        {
            // 2 Hz at zero pressure: a half cycle is 250 ms. 50 ms is not
            // enough to toggle; 100 ms steps accumulate (dt clamp is 100 ms)
            // and cross 0.5 after 250 ms total.
            var a = Turbo(fastMs: 100, slowMs: 500);
            Assert.True(InputManager.TickRepeatVcButtonPhase(a, 0f));

            a.TurboLastTickUtc = DateTime.UtcNow.AddMilliseconds(-50);
            Assert.True(InputManager.TickRepeatVcButtonPhase(a, 0f));   // 0.1 cycles: no flip

            a.TurboLastTickUtc = DateTime.UtcNow.AddMilliseconds(-100);
            Assert.True(InputManager.TickRepeatVcButtonPhase(a, 0f));   // 0.3: no flip

            a.TurboLastTickUtc = DateTime.UtcNow.AddMilliseconds(-100);
            Assert.False(InputManager.TickRepeatVcButtonPhase(a, 0f));  // 0.5: flips OFF
        }

        [Fact]
        public void SquareWave_StallResumesCleanly_NoToggleBurst()
        {
            // A 5-second stall at 10 Hz would be 50 half-cycles. The dt clamp
            // (100 ms) and the single-toggle rule make it exactly one flip.
            var a = Turbo(fastMs: 100, slowMs: 500);
            Assert.True(InputManager.TickRepeatVcButtonPhase(a, 1f));

            a.TurboLastTickUtc = DateTime.UtcNow.AddSeconds(-5);
            Assert.False(InputManager.TickRepeatVcButtonPhase(a, 1f));  // one flip, not fifty
            Assert.True(a.TurboPhase < 0.5);
        }

        [Fact]
        public void SquareWave_GateOff_LegacyPathUnchanged()
        {
            // PressureScaledRate false: the original half-interval timestamp
            // flip, exactly the MacroWave1b contract.
            var a = new MacroAction { Type = MacroActionType.RepeatVcButtonWhileHeld, IntervalMs = 100 };
            Assert.True(InputManager.TickRepeatVcButtonPhase(a));
            a.RepeatVcLastToggleUtc = DateTime.UtcNow.AddMilliseconds(-60);
            Assert.False(InputManager.TickRepeatVcButtonPhase(a));
        }

        // ── The one-shot family ──

        [Fact]
        public void OneShot_FreshActivation_FiresImmediately()
        {
            var a = Turbo();
            Assert.True(InputManager.ShouldFireOneShot(a, a.IntervalMs, 1f));
        }

        [Fact]
        public void OneShot_FiresOncePerFullCycle()
        {
            // 10 Hz: one fire per 100 ms. Two 50 ms steps = one fire.
            var a = Turbo(fastMs: 100, slowMs: 500);
            Assert.True(InputManager.ShouldFireOneShot(a, a.IntervalMs, 1f));

            a.TurboLastTickUtc = DateTime.UtcNow.AddMilliseconds(-50);
            Assert.False(InputManager.ShouldFireOneShot(a, a.IntervalMs, 1f));

            a.TurboLastTickUtc = DateTime.UtcNow.AddMilliseconds(-50);
            Assert.True(InputManager.ShouldFireOneShot(a, a.IntervalMs, 1f));
        }

        [Fact]
        public void OneShot_GateOff_LegacyElapsedCheck()
        {
            var a = new MacroAction { Type = MacroActionType.RepeatKeyWhileHeld, IntervalMs = 100 };
            Assert.True(InputManager.ShouldFireOneShot(a, a.IntervalMs, 0f));   // MinValue: immediate
            Assert.False(InputManager.ShouldFireOneShot(a, a.IntervalMs, 0f));  // just fired
            a.RepeatKeyLastFireUtc = DateTime.UtcNow.AddMilliseconds(-150);
            Assert.True(InputManager.ShouldFireOneShot(a, a.IntervalMs, 0f));
        }

        // ── Persistence ──

        [Fact]
        public void Serialization_RoundTripsTheThreeFields()
        {
            var m = new MacroItem { Name = "RT290" };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.RepeatVcButtonWhileHeld,
                IntervalMs = 50,
                PressureScaledRate = true,
                SlowIntervalMs = 1200,
                TurboRateCurve = "Relaxed",
            });
            var data = SettingsService.BuildMacroDataForMacro(m, 0);
            var clone = SettingsService.LoadMacroFromData(data, PadForge.Engine.VirtualControllerType.Xbox, null);
            var a = Assert.Single(clone.Actions);
            Assert.True(a.PressureScaledRate);
            Assert.Equal(1200, a.SlowIntervalMs);
            Assert.Equal("Relaxed", a.TurboRateCurve);
            Assert.Equal(50, a.IntervalMs);
        }

        [Fact]
        public void Serialization_DefaultsStayLegacy()
        {
            var m = new MacroItem { Name = "RT290d" };
            m.Actions.Add(new MacroAction { Type = MacroActionType.RepeatKeyWhileHeld });
            var data = SettingsService.BuildMacroDataForMacro(m, 0);
            var clone = SettingsService.LoadMacroFromData(data, PadForge.Engine.VirtualControllerType.Xbox, null);
            var a = Assert.Single(clone.Actions);
            Assert.False(a.PressureScaledRate);
            Assert.Equal(500, a.SlowIntervalMs);
            Assert.Equal("Linear", a.TurboRateCurve);
        }

        [Fact]
        public void PropertyClamps_Hold()
        {
            var a = new MacroAction();
            a.SlowIntervalMs = 5;
            Assert.Equal(10, a.SlowIntervalMs);
            a.SlowIntervalMs = 99999;
            Assert.Equal(2000, a.SlowIntervalMs);
            a.TurboRateCurve = null;
            Assert.Equal("Linear", a.TurboRateCurve);
        }
    }
}
