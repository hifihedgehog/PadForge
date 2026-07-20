using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PadForge.Common.Input;

namespace PadForge.Services
{
    /// <summary>
    /// Monitors the foreground window and fires an event when the foreground
    /// process matches a profile's executable list. Called at 30Hz from the
    /// UI timer in <see cref="InputService"/>.
    /// </summary>
    public class ForegroundMonitorService
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        private string _lastExePath;
        private string _lastMatchedProfileId;

        /// <summary>
        /// Set by manual profile switch (shortcut or UI). While true,
        /// the foreground monitor won't re-trigger the same auto-profile.
        /// Cleared when the foreground exe matches a different profile.
        /// </summary>
        public bool ManualOverrideActive { get; private set; }
        private string _overrideProfileId;

        /// <summary>Last foreground exe path observed. Read-only UI feed
        /// (#175 item 8), only tracked while auto-switching is enabled
        /// (<see cref="CheckForegroundWindow"/> early-returns otherwise).</summary>
        public string LastForegroundExePath => _lastExePath;

        /// <summary>Profile id the last foreground exe matched, or null.</summary>
        public string LastMatchedProfileId => _lastMatchedProfileId;

        /// <summary>
        /// Raised when the foreground process matches a different profile than the
        /// currently active one. The string argument is the profile ID to switch to,
        /// or null to revert to the default profile.
        /// </summary>
        public event Action<string> ProfileSwitchRequired;

        /// <summary>
        /// Checks the foreground window process against all profile executable paths.
        /// Only fires <see cref="ProfileSwitchRequired"/> when the matched profile changes.
        /// </summary>
        public void CheckForegroundWindow()
        {
            if (!SettingsManager.EnableAutoProfileSwitching)
                return;

            var profiles = SettingsManager.Profiles;
            if (profiles == null || profiles.Count == 0)
                return;

            string exePath = GetForegroundExePath();
            if (exePath == _lastExePath)
                return; // Same process — skip redundant lookups.

            _lastExePath = exePath;

            // Find matching profile.
            string matchedId = null;
            if (!string.IsNullOrEmpty(exePath))
            {
                foreach (var profile in profiles)
                {
                    if (MatchesExecutables(exePath, profile.ExecutableNames))
                    {
                        matchedId = profile.Id;
                        break;
                    }
                }
            }

            // Only fire if the matched profile changed.
            if (matchedId != _lastMatchedProfileId)
            {
                _lastMatchedProfileId = matchedId;

                if (ManualOverrideActive)
                {
                    if (matchedId == _overrideProfileId)
                        return; // Same auto-profile the user overrode — don't re-trigger.
                    // Different profile or default — user moved on, clear override.
                    ManualOverrideActive = false;
                    _overrideProfileId = null;
                }

                ProfileSwitchRequired?.Invoke(matchedId);
            }
        }

        /// <summary>
        /// Sets the manual override flag so auto-switching won't re-trigger
        /// the same profile the user manually overrode.
        /// </summary>
        public void SetManualOverride(string currentProfileId)
        {
            ManualOverrideActive = true;
            _overrideProfileId = currentProfileId;
        }

        // (hwnd, pid) of the last resolved foreground window. Foreground
        // changes are human-rate; the expensive process query (a full PID
        // snapshot inside GetProcessById plus a module enumeration inside
        // MainModule, ~1-2 KB of allocs) ran at 30 Hz on the UI thread
        // before the exe-path dedup could fire because the dedup needs
        // the path. Two near-free syscalls now gate it.
        private static IntPtr _lastFgHwnd;
        private static uint _lastFgPid;
        private static string _lastFgPath;

        private static string GetForegroundExePath()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                    return null;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0)
                    return null;

                if (hwnd == _lastFgHwnd && pid == _lastFgPid)
                    return _lastFgPath;

                string path;
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    path = proc.MainModule?.FileName;
                }
                catch
                {
                    // Elevated / protected target: cache the null so the
                    // failing open isn't retried 30x a second either.
                    path = null;
                }
                _lastFgHwnd = hwnd;
                _lastFgPid = pid;
                _lastFgPath = path;
                return path;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Matches the foreground process path against a pipe-separated list of
        /// full executable paths (e.g. "C:\Games\game.exe|D:\Other\game2.exe").
        /// </summary>
        private static bool MatchesExecutables(string foregroundPath, string executables)
        {
            if (string.IsNullOrEmpty(executables))
                return false;

            var parts = executables.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var exePath in parts)
            {
                if (string.Equals(exePath, foregroundPath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
