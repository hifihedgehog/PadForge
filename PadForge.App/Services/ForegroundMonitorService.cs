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
                ProfileSwitchRequired?.Invoke(matchedId);
            }
        }

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

                using var proc = Process.GetProcessById((int)pid);
                return proc.MainModule?.FileName;
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
