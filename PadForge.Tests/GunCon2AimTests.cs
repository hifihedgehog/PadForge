using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Namco GunCon 2 (hifihedgehog/SDL#33 Part 9). The fork posts its raw
    /// beam counts on joystick axes 0 and 1, and SdlDeviceWrapper.ApplyGunCon2
    /// scales them to the screen over the PC tools' starting window, X 175 to
    /// 720 and Y 20 to 240, into the stick axes and the IR pointer state.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class GunCon2AimTests
    {
        [Theory]
        [InlineData(175, 20, -1f, -1f)]      // top left of the window
        [InlineData(720, 240, 1f, 1f)]       // bottom right
        [InlineData(284, 130, -0.6f, 0f)]    // a fifth of the way across, level with the middle
        [InlineData(160, 15, -1f, -1f)]      // past the window: clamped, still on screen
        [InlineData(760, 255, 1f, 1f)]
        public void BeamCounts_ScaleToTheScreen(int rawX, int rawY, float x, float y)
        {
            var (ax, ay, on) = SdlDeviceWrapper.GunCon2Aim((short)rawX, (short)rawY);
            Assert.True(on);
            Assert.Equal(x, ax, precision: 3);
            Assert.Equal(y, ay, precision: 3);
        }

        /// <summary>GunconUSB reads X 0 as off screen and counts only X past 10
        /// and Y past 5 as on it, an idle gun with no CRT reports X 1 and Y 5,
        /// and PCSX2 emulates X 0 and Y 0.</summary>
        [Theory]
        [InlineData(0, 120)]
        [InlineData(1, 5)]
        [InlineData(0, 0)]
        [InlineData(10, 120)]
        [InlineData(300, 5)]
        public void NearZeroCounts_AreOffScreen(int rawX, int rawY)
        {
            var (ax, ay, on) = SdlDeviceWrapper.GunCon2Aim((short)rawX, (short)rawY);
            Assert.False(on);
            Assert.Equal(0f, ax);
            Assert.Equal(0f, ay);
        }

        [Fact]
        public void TheStickAxes_SpanTheirRange_AndCenterOffScreen()
        {
            var s = new CustomInputState();
            SdlDeviceWrapper.ApplyGunCon2(s, 175, 240);
            Assert.Equal(1, s.Axis[0]);       // left edge, -32767 stored unsigned
            Assert.Equal(65535, s.Axis[1]);   // bottom edge: SDL's +Y is down
            Assert.True(s.Ir.Detected);

            SdlDeviceWrapper.ApplyGunCon2(s, 0, 120);
            Assert.Equal(32768, s.Axis[0]);
            Assert.Equal(32768, s.Axis[1]);
            Assert.False(s.Ir.Detected);
        }

        /// <summary>The IR pointer read stretches a Wii Remote's aim by
        /// IrMarginStretch. The gun's aim is stored divided by it, so the gun's
        /// sources read the window's edges as the screen's edges.</summary>
        [Fact]
        public void ThePointerSources_ReadTheScreenEdges()
        {
            var oldTuning = SourceCoercion.IrTuningProvider;
            SourceCoercion.IrTuningProvider = null;
            try
            {
                var s = new CustomInputState();
                SdlDeviceWrapper.ApplyGunCon2(s, 720, 20);
                var px = new MappingSource { Descriptor = "IR Pointer X" };
                var py = new MappingSource { Descriptor = "IR Pointer Y" };
                Assert.Equal(1f, SourceCoercion.EvaluateForBipolarAxisTarget(s, px), precision: 4);
                Assert.Equal(-1f, SourceCoercion.EvaluateForBipolarAxisTarget(s, py), precision: 4);

                SdlDeviceWrapper.ApplyGunCon2(s, 311, 185);
                var (x, y, _) = SdlDeviceWrapper.GunCon2Aim(311, 185);
                Assert.Equal(x, SourceCoercion.EvaluateForBipolarAxisTarget(s, px), precision: 4);
                Assert.Equal(y, SourceCoercion.EvaluateForBipolarAxisTarget(s, py), precision: 4);
            }
            finally
            {
                SourceCoercion.IrTuningProvider = oldTuning;
            }
        }

        [Fact]
        public void IsGunCon2_IsTheId()
        {
            Assert.True(new UserDevice { VendorId = 0x0B9A, ProdId = 0x016A }.IsGunCon2);
            Assert.False(new UserDevice { VendorId = 0x0B9A, ProdId = 0x0800 }.IsGunCon2); // GunCon 3
            Assert.False(new UserDevice { VendorId = 0x057E, ProdId = 0x016A }.IsGunCon2);
        }

        /// <summary>The gun gets the pointer sources under its own names, and a
        /// Wii Remote keeps the IR names.</summary>
        [Fact]
        public void ThePicker_OffersTheGunsAimUnderItsOwnNames()
        {
            var si = PadForge.Resources.Strings.Strings.Instance;
            var gun = new UserDevice { VendorId = 0x0B9A, ProdId = 0x016A, ProductName = "Namco GunCon 2" };
            var choices = PadForge.Common.MappingDisplayResolver.BuildInputChoices(gun);
            Assert.Equal(si.Mapping_GunAimX, choices.Single(c => c.Descriptor == "IR Pointer X").DisplayName);
            Assert.Equal(si.Mapping_GunAimY, choices.Single(c => c.Descriptor == "IR Pointer Y").DisplayName);
            Assert.Equal(si.Mapping_GunOffscreen, choices.Single(c => c.Descriptor == "IR Offscreen").DisplayName);

            var remote = new UserDevice { VendorId = 0x057E, ProdId = 0x0306, ProductName = "Nintendo Wii Remote" };
            var remoteChoices = PadForge.Common.MappingDisplayResolver.BuildInputChoices(remote);
            Assert.Equal(si.Mapping_IrPointerX, remoteChoices.Single(c => c.Descriptor == "IR Pointer X").DisplayName);
        }
    }
}
