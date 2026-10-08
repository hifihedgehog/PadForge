using PadForge.ViewModels;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The color one mouse, keyboard or lighting row shows for its slot
    /// (#494): the Lighting tab's whole mode set through the color core every
    /// lit device shares (<see cref="Ds4EffectSynthesizer.ResolveLightbarRgb"/>),
    /// under the game's lightbar the way a DualSense takes it.
    ///
    /// <para>The DualSense's order (Ds5EffectSynthesizer.BuildFields): a
    /// lightbar the game wrote within the grace window wins over every
    /// setting, a macro color included. Once that window closes, a device
    /// left at the player-number default keeps the game's last color rather
    /// than going back to the player color, which is the stand-down the
    /// DualSense makes so a game that sets the bar once is not overwritten
    /// 1.5 s later (#191). Anything the user configured comes back.</para>
    /// </summary>
    internal static class PeripheralLightingColor
    {
        internal static void Resolve(DeviceSlotConfig cfg, int? freshGame, int? lastGame,
            float audioPeak, long nowMs, uint randomColor, uint pulseColor, float pulseIntensity,
            byte batteryPercent, int playerNumber, out byte r, out byte g, out byte b)
        {
            if (freshGame.HasValue)
            {
                Unpack(freshGame.Value, out r, out g, out b);
                return;
            }
            bool unconfigured = cfg == null
                || (cfg.ComputeMacroOverrideIntensity() <= 0f
                    && cfg.LightbarMode == LightbarMode.PlayerNumber
                    && cfg.InputReactiveMode == InputReactiveMode.Off);
            if (unconfigured && lastGame.HasValue)
            {
                Unpack(lastGame.Value, out r, out g, out b);
                return;
            }
            Ds4EffectSynthesizer.ResolveLightbarRgb(cfg, audioPeak, nowMs, randomColor, pulseColor,
                pulseIntensity, batteryPercent, playerNumber, out r, out g, out b);
        }

        private static void Unpack(int rgb, out byte r, out byte g, out byte b)
        {
            r = (byte)(rgb >> 16);
            g = (byte)(rgb >> 8);
            b = (byte)rgb;
        }
    }
}
