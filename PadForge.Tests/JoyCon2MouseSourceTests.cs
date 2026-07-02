using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Engine-level coverage for the #154 Joy-Con 2 optical mouse "Mouse
    /// Motion X/Y" sources. The SDL fork's BLE Switch 2 driver posts the
    /// sensor's absolute 16-bit counters on joystick axes 6/7 (SDL#8); the
    /// wrapper derives per-poll wraparound deltas into
    /// CustomInputState.JoyCon2MouseDX/DY, which is the surface these tests
    /// drive. Full scale is 16 counts per poll, matching SdlMouseWrapper's
    /// MotionScale (2048 per count over the 0..65535 axis range) so the
    /// sensor and a real mouse feel identical through the grid.
    /// </summary>
    public class JoyCon2MouseSourceTests
    {
        private static MappingSource Src(string axis = "X", int deadZone = 0, bool invert = false) => new()
        {
            Descriptor = "Mouse Motion " + axis,
            DeadZone = deadZone,
            Invert = invert,
        };

        private static CustomInputState State(float dx = 0f, float dy = 0f) => new()
        {
            JoyCon2MouseDX = dx,
            JoyCon2MouseDY = dy,
        };

        [Theory]
        [InlineData(0f, 0f)]       // idle
        [InlineData(8f, 0.5f)]     // half deflection at 8 counts/poll
        [InlineData(16f, 1.0f)]    // full scale
        [InlineData(-16f, -1.0f)]  // full scale left
        [InlineData(64f, 1.0f)]    // clamped past full scale
        public void BipolarRead_ScalesCountsLikeARealMouse(float counts, float expected)
        {
            float v = SourceCoercion.EvaluateForBipolarAxisTarget(State(dx: counts), Src("X"));
            Assert.Equal(expected, v, precision: 5);
        }

        [Fact]
        public void YAxis_ReadsTheYDelta()
        {
            float v = SourceCoercion.EvaluateForBipolarAxisTarget(State(dy: 8f), Src("Y"));
            Assert.Equal(0.5f, v, precision: 5);
        }

        [Theory]
        [InlineData(0f, false)]   // still: below any threshold
        [InlineData(2f, false)]   // slow drift: under the 25% threshold
        [InlineData(8f, true)]    // half deflection: above 25%
        [InlineData(-8f, true)]   // direction-blind by default
        public void MotionAsButton_FiresAboveThreshold(float counts, bool expected)
        {
            bool pressed = SourceCoercion.EvaluateForButtonTarget(
                State(dx: counts), Src("X"), globalThresholdPercent: 25);
            Assert.Equal(expected, pressed);
        }

        [Theory]
        [InlineData(8f, false, false)]   // right motion, left-only row: no fire
        [InlineData(-8f, false, true)]   // left motion, left-only row: fires
        [InlineData(-8f, true, false)]   // left motion, right-only row: no fire
        [InlineData(8f, true, true)]     // right motion, right-only row: fires
        public void DirectionalButton_HalfAxisPicksOneDirection(float counts, bool rightOnly, bool expected)
        {
            // The issue's four-direction weapon wheel: HalfAxis = one
            // direction, Invert picks which (Invert = left/up). The wrapper
            // must NOT double-flip (Mouse Motion internalizes Invert like the
            // generic Axis case).
            var src = Src("X", deadZone: 25);
            src.HalfAxis = true;
            src.Invert = !rightOnly;
            bool pressed = SourceCoercion.EvaluateForButtonTarget(
                State(dx: counts), src, globalThresholdPercent: 25);
            Assert.Equal(expected, pressed);
        }

        [Fact]
        public void BidirectionalHalfAxis_RestoresEitherDirection()
        {
            // The UI's "Either" checkbox contract: with HalfAxis on,
            // Bidirectional fires on absolute deflection either side and
            // Invert has no effect.
            var src = Src("X", deadZone: 25);
            src.HalfAxis = true;
            src.Bidirectional = true;
            src.Invert = true; // must be irrelevant in this mode
            Assert.True(SourceCoercion.EvaluateForButtonTarget(State(dx: 8f), src, 25));
            Assert.True(SourceCoercion.EvaluateForButtonTarget(State(dx: -8f), src, 25));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(State(dx: 2f), src, 25));
        }

        [Fact]
        public void DirectionalTrigger_HalfAxisPullsOneWay()
        {
            // "Up/down movements control the trigger 0-100%": HalfAxis+Invert
            // on Mouse Motion Y = up-motion pulls, down-motion rests at 0.
            var src = Src("Y");
            src.HalfAxis = true;
            src.Invert = true; // up (negative Y) pulls
            Assert.Equal(0.5f, SourceCoercion.EvaluateForTriggerTarget(State(dy: -8f), src), precision: 5);
            Assert.Equal(0f, SourceCoercion.EvaluateForTriggerTarget(State(dy: 8f), src), precision: 5);
        }

        [Fact]
        public void PerRowDeadZone_OverridesGlobalThreshold()
        {
            // 50% deflection: above a 25% global threshold but below a 60%
            // per-row DeadZone override, so the override must win. This is the
            // issue's "invisible weapon wheel" knob.
            var state = State(dx: 8f);
            Assert.True(SourceCoercion.EvaluateForButtonTarget(state, Src("X"), 25));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(state, Src("X", deadZone: 60), 25));
        }

        [Fact]
        public void MotionAsTrigger_IsSpeedMagnitude()
        {
            // Trigger read is direction-blind speed (same Math.Abs contract as
            // the IR Pointer trigger read): down-motion pulls as hard as up.
            Assert.Equal(0.5f, SourceCoercion.EvaluateForTriggerTarget(State(dy: 8f), Src("Y")), precision: 5);
            Assert.Equal(0.5f, SourceCoercion.EvaluateForTriggerTarget(State(dy: -8f), Src("Y")), precision: 5);
        }

        [Fact]
        public void Sensitivity_ScalesTheRead()
        {
            var src = Src("X");
            src.IrPointerSensitivity = 2.0;
            float v = SourceCoercion.EvaluateForBipolarAxisTarget(State(dx: 4f), src);
            Assert.Equal(0.5f, v, precision: 5); // 4/16 * 2.0
        }

        [Fact]
        public void Clone_CopiesMouseDeltas()
        {
            var state = State(dx: 3f, dy: -5f);
            var clone = state.Clone();
            Assert.Equal(3f, clone.JoyCon2MouseDX, precision: 5);
            Assert.Equal(-5f, clone.JoyCon2MouseDY, precision: 5);
        }

        [Fact]
        public void HasJoyCon2Mouse_GatesOnExactJoyCon2Identity()
        {
            // Both Joy-Con 2 halves carry the sensor (BLE driver names at
            // SDL_ble_switch2joystick.c:1328/:1331). The gen-1 Joy-Cons, the
            // Switch 2 Pro/GameCube, and non-Nintendo VIDs must not match,
            // and the match is exact equality (the 74b325bc suffix lesson).
            Assert.True(new UserDevice { VendorId = 0x057E, ProductName = "Nintendo Switch 2 Joy-Con (L)" }.HasJoyCon2Mouse);
            Assert.True(new UserDevice { VendorId = 0x057E, ProductName = "Nintendo Switch 2 Joy-Con (R)" }.HasJoyCon2Mouse);
            Assert.False(new UserDevice { VendorId = 0x057E, ProductName = "Nintendo Switch Joy-Con (R)" }.HasJoyCon2Mouse);
            Assert.False(new UserDevice { VendorId = 0x057E, ProductName = "Nintendo Switch 2 Pro Controller" }.HasJoyCon2Mouse);
            Assert.False(new UserDevice { VendorId = 0x057E, ProductName = "Nintendo Switch 2 GameCube Controller" }.HasJoyCon2Mouse);
            Assert.False(new UserDevice { VendorId = 0x045E, ProductName = "Nintendo Switch 2 Joy-Con (R)" }.HasJoyCon2Mouse);
        }
    }
}
