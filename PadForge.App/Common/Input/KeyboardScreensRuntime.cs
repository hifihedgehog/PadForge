using System;
using PadForge.Common.Input.KeyboardScreens;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The Settings mirror the engine reads for the keyboard screens (issue
    /// #519), <see cref="LogitechGKeysRuntime"/>'s shape: static, written by
    /// <c>SettingsViewModel.KeyboardScreensEnabled</c> on the UI thread, and
    /// read by the poll thread's device sweep, which keeps a row for each
    /// present screen while it is on.
    ///
    /// <para>Off by default. Turning it on loads other vendors' libraries,
    /// opens sessions with their software, and can bind WinUSB to a G19's
    /// screen interface.</para>
    /// </summary>
    internal static class KeyboardScreensRuntime
    {
        private static volatile bool _enabled;
        private static volatile int _version;
        private static volatile KeyboardScreenInfo[] _screens = Array.Empty<KeyboardScreenInfo>();

        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                System.Threading.Interlocked.Increment(ref _version);
            }
        }

        /// <summary>Bumps when the rows must reopen to pick up a change.</summary>
        public static int Version => _version;

        /// <summary>The screens present now, in a stable order, which the
        /// screen host publishes from its worker and the device sweep turns
        /// into rows. Empty while the host is stopped.</summary>
        public static KeyboardScreenInfo[] Screens
        {
            get => _screens;
            set => _screens = value ?? Array.Empty<KeyboardScreenInfo>();
        }
    }
}
