using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Which Wii Remote configurations carry the IR camera's axes. The fork's
    /// WiiRemoteHasIRCamera (SDL_hidapi_wii.c) gives them to the bare remote
    /// and the Nunchuk and Classic Controller configurations only, under the
    /// names SDL_WiiExt_TypeName gives each (SDL_hidapi_wii_ext_proto.c).
    /// </summary>
    public class WiiRemoteIdentityTests
    {
        [Theory]
        [InlineData("Nintendo Wii Remote")]
        [InlineData("Nintendo Wii Remote with Nunchuk")]
        [InlineData("Nintendo Wii Remote with Classic Controller")]
        public void TheRemoteAndItsNunchukAndClassicConfigurations_CarryTheCamera(string name)
        {
            Assert.True(WiiRemoteIdentity.CarriesIrCamera(0x057E, name));
            var ud = new UserDevice { VendorId = 0x057E, ProdId = 0x0306, ProductName = name };
            Assert.True(ud.HasIrCamera);
            Assert.Contains(PadForge.Common.MappingDisplayResolver.BuildInputChoices(ud), c => c.Descriptor == "IR Pointer X");
        }

        /// <summary>A decoded extension spends axes 6 and up on its own
        /// controls (the drum kit's 13 axes hold hit velocities on 6-11), and an
        /// unknown extension, the Wii U Pro and the Balance Board get no IR
        /// axes, so none is offered the pointer.</summary>
        [Theory]
        [InlineData("Nintendo Wii Remote with Guitar")]
        [InlineData("Nintendo Wii Remote with Drum Kit")]
        [InlineData("Nintendo Wii Remote with DJ Turntable")]
        [InlineData("Nintendo Wii Remote with Taiko Drum")]
        [InlineData("Nintendo Wii Remote with uDraw Tablet")]
        [InlineData("Nintendo Wii Remote with Drawsome Tablet")]
        [InlineData("Nintendo Wii Remote with Shinkansen Controller")]
        [InlineData("Nintendo Wii Remote with Unknown Extension")]
        [InlineData("Nintendo Wii U Pro Controller")]
        [InlineData("Nintendo Wii Balance Board")]
        public void EveryOtherConfiguration_HasNoCamera(string name)
        {
            Assert.False(WiiRemoteIdentity.CarriesIrCamera(0x057E, name));
            var ud = new UserDevice { VendorId = 0x057E, ProdId = 0x0306, ProductName = name };
            Assert.False(ud.HasIrCamera);
            Assert.DoesNotContain(PadForge.Common.MappingDisplayResolver.BuildInputChoices(ud), c => c.Descriptor == "IR Pointer X");
        }

        [Fact]
        public void AnotherVendorOrNoName_HasNoCamera()
        {
            Assert.False(WiiRemoteIdentity.CarriesIrCamera(0x054C, "Nintendo Wii Remote"));
            Assert.False(WiiRemoteIdentity.CarriesIrCamera(0x057E, null));
            Assert.False(WiiRemoteIdentity.CarriesIrCamera(0x057E, string.Empty));
            Assert.True(WiiRemoteIdentity.CarriesIrCamera(0x057E, " nintendo wii remote "));
        }
    }
}
