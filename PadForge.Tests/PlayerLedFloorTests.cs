using PadForge.Common.Input;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #191 player-identity idle floor. Tables must stay byte-for-byte
    /// the shipping implementations' (SDL_hidapi_ps5.c SetLedsForPlayerIndex
    /// / SetLightsForPlayerIndex, linux hid-playstation.c player_colors and
    /// dualsense_set_player_leds), and the floor must sit BELOW every
    /// existing precedence layer: external game writes, macro overrides,
    /// and configured modes all win.
    /// </summary>
    public class PlayerLedFloorTests
    {
        [Theory]
        [InlineData(1, 0x00, 0x00, 0x40)] // blue
        [InlineData(2, 0x40, 0x00, 0x00)] // red
        [InlineData(3, 0x00, 0x40, 0x00)] // green
        [InlineData(4, 0x20, 0x00, 0x20)] // pink
        [InlineData(5, 0x20, 0x10, 0x00)] // orange
        [InlineData(6, 0x00, 0x10, 0x10)] // teal
        [InlineData(7, 0x10, 0x10, 0x10)] // white
        [InlineData(8, 0x00, 0x00, 0x40)] // wraps to blue
        public void ColorTable_MatchesSdlAndKernel(int player, byte r, byte g, byte b)
        {
            var c = PlayerIdentityDefaults.ColorFor(player);
            Assert.Equal((r, g, b), c);
        }

        [Theory]
        [InlineData(1, 0x04)]
        [InlineData(2, 0x0A)]
        [InlineData(3, 0x15)]
        [InlineData(4, 0x1B)]
        [InlineData(5, 0x1F)]
        [InlineData(6, 0x11)]
        [InlineData(7, 0x0E)]
        [InlineData(8, 0x04)] // wraps
        public void PipTable_MatchesSdlAndKernel(int player, byte pips)
        {
            Assert.Equal(pips, PlayerIdentityDefaults.PipsFor(player));
        }

        private static PlayStationSlotConfig UntouchedConfig() => new PlayStationSlotConfig();

        [Fact]
        public void Ds5_UntouchedConfig_FloorShowsPlayerColorAndPips()
        {
            var fields = Ds5EffectSynthesizer.BuildFields(UntouchedConfig(), playerNumber: 3);

            var lightbar = (byte[])fields["lightbar"];
            Assert.Equal(new byte[] { 0x00, 0x40, 0x00 }, lightbar);
            byte pi = (byte)fields["playerIndicator"];
            Assert.Equal((byte)(0x20 | 0x15), pi);
        }

        [Fact]
        public void Ds5_NoPlayerNumber_KeepsLegacyIdle()
        {
            var fields = Ds5EffectSynthesizer.BuildFields(UntouchedConfig(), playerNumber: 0);
            Assert.Equal((byte)0x20, (byte)fields["playerIndicator"]); // extinguished, no-fade only
        }

        [Fact]
        public void Ds5_ConfiguredPlayerLed_BeatsTheFloor()
        {
            var cfg = UntouchedConfig();
            cfg.PlayerLedMode = PlayerLedMode.Player2;
            var fields = Ds5EffectSynthesizer.BuildFields(cfg, playerNumber: 3);
            Assert.Equal((byte)(0x20 | 0x0A), (byte)fields["playerIndicator"]);
        }

        [Fact]
        public void Ds5_ConfiguredLightbarMode_BeatsTheFloor()
        {
            var cfg = UntouchedConfig();
            cfg.LightbarMode = LightbarMode.Static;
            cfg.LightbarRed = 1; cfg.LightbarGreen = 2; cfg.LightbarBlue = 3;
            var fields = Ds5EffectSynthesizer.BuildFields(cfg, playerNumber: 3);
            var lightbar = (byte[])fields["lightbar"];
            Assert.Equal(new byte[] { 1, 2, 3 }, lightbar);
        }

        [Fact]
        public void Ds5_ExternalGameWrite_BeatsTheFloor()
        {
            var overrides = new UserEffectsDispatcher.ExternalSubsystemOverrides
            {
                LightbarRgb = new byte[] { 9, 8, 7 },
                PlayerIndicator = 0x2F,
            };
            var fields = Ds5EffectSynthesizer.BuildFields(
                UntouchedConfig(), overrides: overrides, playerNumber: 3);
            Assert.Equal(new byte[] { 9, 8, 7 }, (byte[])fields["lightbar"]);
            Assert.Equal((byte)0x2F, (byte)fields["playerIndicator"]);
        }

        [Fact]
        public void Ds5_GameEverWroteLightbar_FloorStandsDown_PipsStay()
        {
            // A game wrote the lightbar earlier this session but its
            // grace window lapsed: the floor must NOT reclaim the bar
            // (the game's last write persists in firmware because the
            // enable bit stays clear), while the pip floor still shows
            // the player number (pips never had persistence semantics).
            var overrides = new UserEffectsDispatcher.ExternalSubsystemOverrides
            {
                LightbarEverExternal = true,
            };
            var fields = Ds5EffectSynthesizer.BuildFields(
                UntouchedConfig(), overrides: overrides, playerNumber: 3);
            Assert.Equal(new byte[] { 0, 0, 0 }, (byte[])fields["lightbar"]);
            Assert.Equal((byte)(0x20 | 0x15), (byte)fields["playerIndicator"]);
        }

        [Fact]
        public void Ds4_UntouchedConfig_FloorShowsPlayerColor_InsteadOfBlack()
        {
            var fields = Ds4EffectSynthesizer.BuildFields(
                UntouchedConfig(), 0f, 0, 0, 0, 0f, 0, 0, playerNumber: 4);
            Assert.Equal(new byte[] { 0x20, 0x00, 0x20 }, (byte[])fields["lightbar"]);
        }

        [Fact]
        public void Ds4_NoPlayerNumber_StaysBlack()
        {
            var fields = Ds4EffectSynthesizer.BuildFields(
                UntouchedConfig(), 0f, 0, 0, 0, 0f, 0, 0, playerNumber: 0);
            Assert.Equal(new byte[] { 0, 0, 0 }, (byte[])fields["lightbar"]);
        }

        [Fact]
        public void Ds4_ConfiguredStatic_BeatsTheFloor()
        {
            var cfg = UntouchedConfig();
            cfg.LightbarMode = LightbarMode.Static;
            cfg.LightbarRed = 5; cfg.LightbarGreen = 6; cfg.LightbarBlue = 7;
            var fields = Ds4EffectSynthesizer.BuildFields(
                cfg, 0f, 0, 0, 0, 0f, 0, 0, playerNumber: 4);
            Assert.Equal(new byte[] { 5, 6, 7 }, (byte[])fields["lightbar"]);
        }

        [Fact]
        public void Ds4_ExternalGameWrite_BeatsTheFloor()
        {
            var overrides = new UserEffectsDispatcher.ExternalSubsystemOverrides
            {
                LightbarRgb = new byte[] { 3, 2, 1 },
            };
            var fields = Ds4EffectSynthesizer.BuildFields(
                UntouchedConfig(), 0f, 0, 0, 0, 0f, 0, 0,
                overrides: overrides, playerNumber: 4);
            Assert.Equal(new byte[] { 3, 2, 1 }, (byte[])fields["lightbar"]);
        }
    }
}
