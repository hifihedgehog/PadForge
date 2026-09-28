namespace PadForge.Common.Input
{
    /// <summary>
    /// The Settings mirror the engine reads for analog keyboards (issue
    /// #468), the <see cref="LogitechGKeysRuntime"/> shape: static, written by
    /// <c>SettingsViewModel.AnalogKeyboardsEnabled</c> on the UI thread, read
    /// by the poll thread's device sweep.
    ///
    /// <para>Off by default. The polled families talk over the channel their
    /// vendors' configurators use, VIA for the Keychron and Lemokey boards
    /// among them, and nobody who has not asked for it should have PadForge
    /// polling there.</para>
    /// </summary>
    internal static class AnalogKeyboardRuntime
    {
        private static volatile bool _enabled;

        /// <summary>Whether analog keyboards are read.</summary>
        public static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }
    }
}
