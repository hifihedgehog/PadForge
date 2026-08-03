using System;
using PadForge.Engine.Common.Mapping;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #271 item 5: the tilt-compensated heading core, ported from the
    /// cloned x-io Fusion reference (FusionCompass.c:24-29, NWU). Pinned
    /// against synthetic geometry: with up = +z, a field along device +x
    /// reads heading 0 (facing magnetic north), rotations about up shift
    /// the heading oppositely, and degenerate vectors return null.
    /// </summary>
    public class CompassHeadingTests
    {
        private const float UpZ = 1f;

        [Fact]
        public void FieldAlongDeviceX_ReadsZeroHeading()
        {
            float? h = SourceCoercion.ComputeTiltCompensatedHeading(0, 0, UpZ, 1, 0, 0);
            Assert.NotNull(h);
            Assert.Equal(0f, h.Value, 4);
        }

        [Fact]
        public void FieldAlongDeviceY_ReadsMinusHalfPi()
        {
            float? h = SourceCoercion.ComputeTiltCompensatedHeading(0, 0, UpZ, 0, 1, 0);
            Assert.NotNull(h);
            Assert.Equal(-MathF.PI / 2f, h.Value, 4);
        }

        [Fact]
        public void OppositeField_ReadsPi()
        {
            float? h = SourceCoercion.ComputeTiltCompensatedHeading(0, 0, UpZ, -1, 0, 0);
            Assert.NotNull(h);
            Assert.Equal(MathF.PI, Math.Abs(h.Value), 4);
        }

        [Theory]
        [InlineData(0.5f)]
        [InlineData(1.2f)]
        [InlineData(-2.0f)]
        public void RotatingTheFieldAboutUp_ShiftsTheHeadingOppositely(float theta)
        {
            // The device turning by +theta makes the fixed earth field
            // appear rotated by -theta in device axes. Feed the rotated
            // field and expect heading +theta (wrapped).
            float mx = MathF.Cos(-theta), my = MathF.Sin(-theta);
            float? h = SourceCoercion.ComputeTiltCompensatedHeading(0, 0, UpZ, mx, my, 0);
            Assert.NotNull(h);
            float diff = MathF.Abs(MathF.IEEERemainder(h.Value - theta, 2f * MathF.PI));
            Assert.True(diff < 1e-3f, $"heading {h.Value} vs expected {theta}");
        }

        [Fact]
        public void TiltCompensation_IgnoresTheVerticalFieldComponent()
        {
            // Same horizontal field with a big vertical (dip) component:
            // the heading must match the flat case. This is the whole
            // point of the cross-product construction.
            float? flat = SourceCoercion.ComputeTiltCompensatedHeading(0, 0, UpZ, 1, 0, 0);
            float? dipped = SourceCoercion.ComputeTiltCompensatedHeading(0, 0, UpZ, 1, 0, -3f);
            Assert.NotNull(flat);
            Assert.NotNull(dipped);
            Assert.Equal(flat.Value, dipped.Value, 4);
        }

        [Fact]
        public void DegenerateVectors_ReturnNull()
        {
            // Zero mag, zero accel, and field parallel to gravity (no
            // horizontal component to take a heading from).
            Assert.Null(SourceCoercion.ComputeTiltCompensatedHeading(0, 0, UpZ, 0, 0, 0));
            Assert.Null(SourceCoercion.ComputeTiltCompensatedHeading(0, 0, 0, 1, 0, 0));
            Assert.Null(SourceCoercion.ComputeTiltCompensatedHeading(0, 0, UpZ, 0, 0, 5f));
        }
    }
}
