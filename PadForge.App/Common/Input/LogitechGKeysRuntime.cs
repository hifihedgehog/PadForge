namespace PadForge.Common.Input
{
    /// <summary>
    /// The Settings mirror the engine reads for Logitech G-keys (issue #454),
    /// the shape <see cref="HeadTrackingRuntime"/> and
    /// <see cref="HandheldButtonRegistry"/> already use: static, written by the
    /// view model on the UI thread, read by the poll thread's device sweep.
    ///
    /// <para>The writer is <c>SettingsViewModel.GKeysEnabled</c>, on the
    /// Settings page's Hardware Support card.</para>
    ///
    /// <para>Off by default. Turning it on loads a third-party library and
    /// starts a session with the Logitech software, which nobody who has not
    /// asked for it should pay for.</para>
    /// </summary>
    internal static class LogitechGKeysRuntime
    {
        private static volatile bool _enabled;
        private static volatile int _version;

        /// <summary>Whether G-keys are read through the G-key SDK.</summary>
        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                // Interlocked, not ++. A volatile read-modify-write is still
                // two operations, and a lost bump means the poll thread never
                // reopens the row.
                System.Threading.Interlocked.Increment(ref _version);
            }
        }

        /// <summary>Bumps when the row must reopen to pick up a change.</summary>
        public static int Version => _version;
    }
}
