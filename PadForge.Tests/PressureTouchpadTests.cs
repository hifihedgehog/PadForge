using System;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #239 (pressure-sensitive touchpads): windowed pressure
    /// reads, the exclusive five-zone DS3-sim resolver, pressure as a
    /// button / shift-activator threshold, and the synthetic pressure
    /// curve for pads whose hardware reports touch as pressure 1.0.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class PressureTouchpadTests : IDisposable
    {
        public PressureTouchpadTests() => SourceCoercion.TouchpadSyntheticPressureProvider = null;
        public void Dispose() => SourceCoercion.TouchpadSyntheticPressureProvider = null;

        private static CustomInputState PadState(float x, float y, float pressure,
            bool down = true, bool clicked = false)
        {
            var tp = new TouchpadInputState(2);
            tp.FingerDown[0] = down;
            tp.FingerX[0] = x;
            tp.FingerY[0] = y;
            tp.FingerPressure[0] = pressure;
            tp.Clicked = clicked;
            return new CustomInputState { Touchpads = new[] { tp } };
        }

        // Unique device identity for the synthetic-provider tests: the
        // provider is STATIC GLOBAL state and xUnit runs test classes in
        // parallel, so an unscoped (true, ...) provider would leak the
        // synthesis into every other pressure test running concurrently.
        private const string TestDeviceGuid = "239f0000-0000-0000-0000-000000000239";

        private static MappingSource Src(string descriptor, int deadZone = 0)
            => new() { Descriptor = descriptor, DeadZone = deadZone, DeviceGuid = TestDeviceGuid };

        private static Func<string, int, int, (bool, float)> ScopedProvider(float touchLevel)
            => (guid, _, _) => (string.Equals(guid, TestDeviceGuid, StringComparison.OrdinalIgnoreCase), touchLevel);

        // ─── Windowed pressure (mode 2) ───

        [Fact]
        public void WindowedPressure_ReadsInsideTheWindow_NeutralOutside()
        {
            // Finger on the upper half at 70% pressure.
            var state = PadState(0.5f, 0.2f, 0.7f);
            float up = SourceCoercion.EvaluateForTriggerTarget(state, Src("Touchpad 0 Finger 0 Pressure Upper"), 0);
            Assert.Equal(0.7f, up, 2);
            float low = SourceCoercion.EvaluateForTriggerTarget(state, Src("Touchpad 0 Finger 0 Pressure Lower"), 0);
            Assert.Equal(0f, low, 2);
        }

        // ─── Exclusive five-zone resolution (mode 3, DS3 sim) ───

        [Fact]
        public void ResolveFiveZone_CenterWinsInsideTheRadius()
        {
            Assert.Equal(SourceCoercion.TouchpadZoneCenter, SourceCoercion.ResolveFiveZone(0.5f, 0.5f));
            Assert.Equal(SourceCoercion.TouchpadZoneCenter, SourceCoercion.ResolveFiveZone(0.6f, 0.6f));
            Assert.Equal(SourceCoercion.TouchpadQuadNorth, SourceCoercion.ResolveFiveZone(0.5f, 0.05f));
            Assert.Equal(SourceCoercion.TouchpadQuadSouth, SourceCoercion.ResolveFiveZone(0.5f, 0.95f));
            Assert.Equal(SourceCoercion.TouchpadQuadWest, SourceCoercion.ResolveFiveZone(0.05f, 0.5f));
            Assert.Equal(SourceCoercion.TouchpadQuadEast, SourceCoercion.ResolveFiveZone(0.95f, 0.5f));
        }

        [Fact]
        public void CenterPress_NeverAlsoFiresAnOuterZone()
        {
            // Finger dead center at full pressure: the Center descriptor
            // reads it, all four quadrant pressure descriptors stay 0.
            var state = PadState(0.5f, 0.5f, 1f);
            Assert.Equal(1f, SourceCoercion.EvaluateForTriggerTarget(
                state, Src("Touchpad 0 Finger 0 Pressure Center"), 0), 2);
            foreach (var zone in new[] { "North", "South", "East", "West" })
                Assert.Equal(0f, SourceCoercion.EvaluateForTriggerTarget(
                    state, Src($"Touchpad 0 Finger 0 Pressure {zone}"), 0), 2);
        }

        [Fact]
        public void OuterZonePress_NeverAlsoFiresCenter()
        {
            var state = PadState(0.5f, 0.05f, 0.8f);   // deep in North
            Assert.Equal(0.8f, SourceCoercion.EvaluateForTriggerTarget(
                state, Src("Touchpad 0 Finger 0 Pressure North"), 0), 2);
            Assert.Equal(0f, SourceCoercion.EvaluateForTriggerTarget(
                state, Src("Touchpad 0 Finger 0 Pressure Center"), 0), 2);
        }

        [Fact]
        public void CenterWindow_IsPressureOnlyGrammar()
        {
            // "X Center" is not a valid window; the descriptor must not
            // parse as a touchpad axis (falls through to bool-and-false).
            var state = PadState(0.5f, 0.5f, 1f);
            Assert.Equal(0f, SourceCoercion.EvaluateForTriggerTarget(
                state, Src("Touchpad 0 Finger 0 X Center"), 0), 2);
        }

        // ─── Pressure as button / shift activator (mode 1) ───

        [Fact]
        public void PressureButton_FiresPastTheThreshold()
        {
            var deep = PadState(0.5f, 0.5f, 0.7f);
            var light = PadState(0.5f, 0.5f, 0.3f);
            // Per-source DeadZone 60: the "pressed 60% = layer" ask.
            Assert.True(SourceCoercion.EvaluateForButtonTarget(
                deep, Src("Touchpad 0 Finger 0 Pressure", deadZone: 60), 25));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(
                light, Src("Touchpad 0 Finger 0 Pressure", deadZone: 60), 25));
            // Global threshold fallback.
            Assert.True(SourceCoercion.EvaluateForButtonTarget(
                light, Src("Touchpad 0 Finger 0 Pressure"), 25));
        }

        [Fact]
        public void PressureButton_RespectsZoneWindows()
        {
            var north = PadState(0.5f, 0.05f, 0.9f);
            Assert.True(SourceCoercion.EvaluateForButtonTarget(
                north, Src("Touchpad 0 Finger 0 Pressure North", deadZone: 50), 25));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(
                north, Src("Touchpad 0 Finger 0 Pressure South", deadZone: 50), 25));
        }

        // ─── Synthetic pressure (the DS4 / DualSense / SC2015 bonus) ───

        [Fact]
        public void SyntheticPressure_ThreeStops()
        {
            SourceCoercion.TouchpadSyntheticPressureProvider = ScopedProvider(0.5f);

            // Hardware reports 1.0 on touch; synthesis remaps to 50%.
            var touching = PadState(0.5f, 0.5f, 1f);
            Assert.Equal(0.5f, SourceCoercion.EvaluateForTriggerTarget(
                touching, Src("Touchpad 0 Finger 0 Pressure"), 0), 2);

            // Pad click = full pressure.
            var clicked = PadState(0.5f, 0.5f, 1f, clicked: true);
            Assert.Equal(1f, SourceCoercion.EvaluateForTriggerTarget(
                clicked, Src("Touchpad 0 Finger 0 Pressure"), 0), 2);

            // No touch = 0 (the reader returns 0 before synthesis anyway).
            var lifted = PadState(0.5f, 0.5f, 0f, down: false);
            Assert.Equal(0f, SourceCoercion.EvaluateForTriggerTarget(
                lifted, Src("Touchpad 0 Finger 0 Pressure"), 0), 2);
        }

        [Fact]
        public void SyntheticPressure_DisabledProvider_PassesRawThrough()
        {
            SourceCoercion.TouchpadSyntheticPressureProvider = (guid, _, _) => (false, 0.5f);
            var state = PadState(0.5f, 0.5f, 0.83f);
            Assert.Equal(0.83f, SourceCoercion.EvaluateForTriggerTarget(
                state, Src("Touchpad 0 Finger 0 Pressure"), 0), 2);
        }

        [Fact]
        public void SyntheticPressure_AppliesToTheButtonRead()
        {
            SourceCoercion.TouchpadSyntheticPressureProvider = ScopedProvider(0.5f);
            var touching = PadState(0.5f, 0.5f, 1f);
            // Touch synthesizes 50%: below a 60% activator threshold.
            Assert.False(SourceCoercion.EvaluateForButtonTarget(
                touching, Src("Touchpad 0 Finger 0 Pressure", deadZone: 60), 25));
            // Click synthesizes 100%: clears it.
            var clicked = PadState(0.5f, 0.5f, 1f, clicked: true);
            Assert.True(SourceCoercion.EvaluateForButtonTarget(
                clicked, Src("Touchpad 0 Finger 0 Pressure", deadZone: 60), 25));
        }

        // ─── DeviceSlotConfig persistence ───

        [Fact]
        public void SyntheticPressureConfig_RoundTripsThroughTheDataDto()
        {
            var cfg = new PadForge.ViewModels.DeviceSlotConfig
            {
                TouchpadSyntheticPressure = true,
                TouchpadSyntheticTouchPercent = 65,
            };
            Assert.True(cfg.TouchpadSyntheticPressure);
            Assert.Equal(65, cfg.TouchpadSyntheticTouchPercent);
            // Clamp contract.
            cfg.TouchpadSyntheticTouchPercent = 150;
            Assert.Equal(100, cfg.TouchpadSyntheticTouchPercent);
        }
    }
}
