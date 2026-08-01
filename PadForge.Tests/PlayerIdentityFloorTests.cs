using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The player-identity lightbar floor stands down for the session once a
    /// game claims the bar. It must NOT stand down for a host merely
    /// assigning a player colour when it opens the pad. A single such write
    /// at enumeration darkened the Lighting tab's Player Number mode for the
    /// whole session once composite personas began enumerating as real USB
    /// devices (observed: rgb 64,0,0 with validFlag0 0x00, validFlag1 0x14).
    /// </summary>
    public class PlayerIdentityFloorTests
    {
        [Theory]
        [InlineData(0x00, 0x14)]   // the observed enumeration write: lightbar + player indicator
        [InlineData(0x00, 0x04)]   // lightbar alone
        [InlineData(0x00, 0x10)]   // player indicator alone
        [InlineData(0x00, 0x01)]   // mic LED alone
        [InlineData(0x00, 0x1D)]   // every identity bit at once
        public void IdentityOnlyWrites_DoNotClaimTheLightbar(byte vf0, byte vf1)
        {
            Assert.True(UserEffectsDispatcher.IsIdentityOnlyLightbarWrite(vf0, vf1));
        }

        [Theory]
        [InlineData(0x01, 0x04)]   // rumble alongside the bar: a game driving effects
        [InlineData(0x04, 0x04)]   // right trigger effect
        [InlineData(0x08, 0x04)]   // left trigger effect
        [InlineData(0xA0, 0x84)]   // the speaker-path assertion shape
        [InlineData(0x00, 0x24)]   // a validFlag1 bit outside the identity set
        [InlineData(0x00, 0x80)]   // audio control 2
        public void RealClaims_StandTheFloorDown(byte vf0, byte vf1)
        {
            Assert.False(UserEffectsDispatcher.IsIdentityOnlyLightbarWrite(vf0, vf1));
        }

        [Fact]
        public void IdentityMask_IsExactlyTheFourIdentityBits()
        {
            // mic LED 0x01, lightbar 0x04, reset lights 0x08, player indicator 0x10.
            Assert.Equal(0x1D, UserEffectsDispatcher.IdentityOnlyVf1Mask);
        }
    }
}
