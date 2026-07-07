using PadForge.Common.Input;
using PadForge.Services;
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

        // ── True Off (stealth) vs the PlayerNumber default ──
        // Off used to double as "unset"; since the PlayerNumber default
        // landed it is a deliberate hard-off and must actually darken.

        [Fact]
        public void Ds5_OffPlayerLed_ExtinguishesDespitePlayerNumber()
        {
            var cfg = UntouchedConfig();
            cfg.PlayerLedMode = PlayerLedMode.Off;
            var fields = Ds5EffectSynthesizer.BuildFields(cfg, playerNumber: 3);
            Assert.Equal((byte)0x20, (byte)fields["playerIndicator"]);
        }

        [Fact]
        public void Ds5_OffLightbar_PaintsBlackAsserted_DespitePlayerNumber()
        {
            var cfg = UntouchedConfig();
            cfg.LightbarMode = LightbarMode.Off;
            var fields = Ds5EffectSynthesizer.BuildFields(cfg, playerNumber: 3);
            Assert.Equal(new byte[] { 0, 0, 0 }, (byte[])fields["lightbar"]);
            // The lightbar enable bit (validFlag1 bit 2) must assert so
            // the firmware applies the black: hard off means dark, not
            // "keep whatever was there".
            Assert.NotEqual(0, (byte)fields["validFlag1"] & 0x04);
        }

        [Fact]
        public void Ds5_UntouchedAfterGameWrite_LeavesEnableClear()
        {
            // Companion to the stand-down test above: with the floor
            // stood down the enable bit stays CLEAR, which is exactly
            // how the game's last write persists in firmware.
            var overrides = new UserEffectsDispatcher.ExternalSubsystemOverrides
            {
                LightbarEverExternal = true,
            };
            var fields = Ds5EffectSynthesizer.BuildFields(
                UntouchedConfig(), overrides: overrides, playerNumber: 3);
            Assert.Equal(0, (byte)fields["validFlag1"] & 0x04);
        }

        [Fact]
        public void Ds5_ConfiguredPips_BarStaysOnTheFloor()
        {
            // Picking a pip pattern must not black out the bar the way
            // the pre-#191 pips-only assert did: the bar is still at
            // its PlayerNumber default, so it shows the player color.
            var cfg = UntouchedConfig();
            cfg.PlayerLedMode = PlayerLedMode.Player2;
            var fields = Ds5EffectSynthesizer.BuildFields(cfg, playerNumber: 3);
            Assert.Equal(new byte[] { 0x00, 0x40, 0x00 }, (byte[])fields["lightbar"]);
            Assert.Equal((byte)(0x20 | 0x0A), (byte)fields["playerIndicator"]);
        }

        // ── Pips must never couple into the lightbar enable gate ──
        // A pips-only choice (stealth Off or a fixed pattern) with the
        // bar left at its PlayerNumber default must not author the
        // lightbar. Once the floor has stood down (a game wrote the bar
        // this session), authoring it would ship black over the game's
        // persisted color. The lightbar enable bit stays CLEAR, exactly
        // as it does with the pips at their default.

        [Fact]
        public void Ds5_PipsOff_DoesNotAuthorLightbar_AfterGameWrite()
        {
            var overrides = new UserEffectsDispatcher.ExternalSubsystemOverrides
            {
                LightbarEverExternal = true,
            };
            var cfg = UntouchedConfig();
            cfg.PlayerLedMode = PlayerLedMode.Off;
            var fields = Ds5EffectSynthesizer.BuildFields(cfg, overrides: overrides, playerNumber: 3);
            Assert.Equal(0, (byte)fields["validFlag1"] & 0x04); // lightbar enable clear
            Assert.Equal((byte)0x20, (byte)fields["playerIndicator"]); // pips extinguished
        }

        [Fact]
        public void Ds5_FixedPips_DoNotAuthorLightbar_AfterGameWrite()
        {
            var overrides = new UserEffectsDispatcher.ExternalSubsystemOverrides
            {
                LightbarEverExternal = true,
            };
            var cfg = UntouchedConfig();
            cfg.PlayerLedMode = PlayerLedMode.Player2;
            var fields = Ds5EffectSynthesizer.BuildFields(cfg, overrides: overrides, playerNumber: 3);
            Assert.Equal(0, (byte)fields["validFlag1"] & 0x04); // lightbar enable clear
            Assert.Equal((byte)(0x20 | 0x0A), (byte)fields["playerIndicator"]); // Player2 pips
        }

        [Fact]
        public void Ds5_PipsOff_FloorArmed_BarStillShowsPlayerColor()
        {
            // The other direction of independence: with no game write,
            // turning the pips off leaves the bar on its player-color
            // floor (the pips choice touches only the pips).
            var cfg = UntouchedConfig();
            cfg.PlayerLedMode = PlayerLedMode.Off;
            var fields = Ds5EffectSynthesizer.BuildFields(cfg, playerNumber: 3);
            Assert.Equal(new byte[] { 0x00, 0x40, 0x00 }, (byte[])fields["lightbar"]);
            Assert.Equal((byte)0x20, (byte)fields["playerIndicator"]);
        }

        [Fact]
        public void Ds4_OffLightbar_PaintsBlack_DespitePlayerNumber()
        {
            var cfg = UntouchedConfig();
            cfg.LightbarMode = LightbarMode.Off;
            var fields = Ds4EffectSynthesizer.BuildFields(
                cfg, 0f, 0, 0, 0, 0f, 0, 0, playerNumber: 4);
            Assert.Equal(new byte[] { 0, 0, 0 }, (byte[])fields["lightbar"]);
        }

        // ── LightingRev migration (SettingsService.ApplyPlayStationConfigData) ──
        // Rev-0 saves (every release before the PlayerNumber default)
        // spelled "unset" as Off; rev-1 saves mean what they say.

        [Fact]
        public void Rev0_OffMeansUnset_LiftsToPlayerNumber()
        {
            var cfg = new PlayStationSlotConfig();
            cfg.LightbarMode = LightbarMode.Static;   // prove the loader overwrites
            cfg.PlayerLedMode = PlayerLedMode.All;
            SettingsService.ApplyPlayStationConfigData(cfg, new PlayStationSlotConfigData());
            Assert.Equal(LightbarMode.PlayerNumber, cfg.LightbarMode);
            Assert.Equal(PlayerLedMode.PlayerNumber, cfg.PlayerLedMode);
        }

        [Fact]
        public void Rev1_OffIsDeliberate_StaysOff()
        {
            var cfg = new PlayStationSlotConfig();
            var d = new PlayStationSlotConfigData
            {
                LightingRev = 1,
                LightbarMode = LightbarMode.Off,
                PlayerLedMode = PlayerLedMode.Off,
            };
            SettingsService.ApplyPlayStationConfigData(cfg, d);
            Assert.Equal(LightbarMode.Off, cfg.LightbarMode);
            Assert.Equal(PlayerLedMode.Off, cfg.PlayerLedMode);
        }

        [Fact]
        public void Rev1_StaleLegacyBools_DontResurrectStatic()
        {
            // LightbarEnabled round-trips forever and is never cleared
            // when the user changes modes: a rev-1 deliberate Off must
            // not fall back to the v3.0 trio.
            var cfg = new PlayStationSlotConfig();
            var d = new PlayStationSlotConfigData
            {
                LightingRev = 1,
                LightbarMode = LightbarMode.Off,
                LightbarEnabled = true,
            };
            SettingsService.ApplyPlayStationConfigData(cfg, d);
            Assert.Equal(LightbarMode.Off, cfg.LightbarMode);
        }

        [Fact]
        public void Rev0_LegacyLightbarEnabled_StillMigratesToStatic()
        {
            var cfg = new PlayStationSlotConfig();
            var d = new PlayStationSlotConfigData { LightbarEnabled = true }; // v3.0 save shape
            SettingsService.ApplyPlayStationConfigData(cfg, d);
            Assert.Equal(LightbarMode.Static, cfg.LightbarMode);
        }

        [Fact]
        public void Rev0_LegacyReactiveBase_KeepsDarkBase()
        {
            // Pre-v3.2 saves stored InputReactive as a base mode. The
            // v3.2 migration splits it into overlay + parked base. Since
            // the overlay is now active, the rev-0 lift must leave the
            // base at Off (dark) rather than brightening it to the
            // player floor, so the reactive-from-darkness effect the
            // user configured survives the upgrade.
            var cfg = new PlayStationSlotConfig();
            var d = new PlayStationSlotConfigData { LightbarMode = LightbarMode.InputReactive };
            SettingsService.ApplyPlayStationConfigData(cfg, d);
            Assert.Equal(InputReactiveMode.Random, cfg.InputReactiveMode);
            Assert.Equal(LightbarMode.Off, cfg.LightbarMode);
        }

        [Fact]
        public void Rev0_IdleSlot_NoOverlay_StillLiftsLightbarToPlayerNumber()
        {
            // The guard on the lift is "no overlay", not "never": a
            // genuinely idle rev-0 slot (Off base, no reactive overlay)
            // still inherits the floor.
            var cfg = new PlayStationSlotConfig();
            var d = new PlayStationSlotConfigData
            {
                LightbarMode = LightbarMode.Off,
                InputReactiveMode = InputReactiveMode.Off,
            };
            SettingsService.ApplyPlayStationConfigData(cfg, d);
            Assert.Equal(LightbarMode.PlayerNumber, cfg.LightbarMode);
        }
    }
}
