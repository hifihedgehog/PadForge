using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The programs that have a window open now, each by its image path:
    /// what Add a Running Game offers in the profile dialog (#574), so a
    /// game is added by picking it and not by finding its file.
    ///
    /// <para>A window counts when the taskbar would show it: it is visible,
    /// has a title, has no owner, is no tool window, and is not cloaked. A
    /// suspended Store app keeps a visible window that Windows cloaks, and
    /// a virtual desktop cloaks the windows of the other desktops.</para>
    /// </summary>
    internal static class WindowedPrograms
    {
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        private const uint GW_OWNER = 4;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int DWMWA_CLOAKED = 14;

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLengthW(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        /// <summary>The image paths of the programs with a window open,
        /// PadForge left out, each once, by file name.</summary>
        internal static IReadOnlyList<string> List()
        {
            var windows = new List<(uint Pid, string Path)>();
            var asked = new HashSet<uint>();
            char[] buffer = null;
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd) || GetWindowTextLengthW(hwnd) <= 0) return true;
                if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
                if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                GetWindowThreadProcessId(hwnd, out uint pid);
                // One query per process, however many windows it has.
                if (pid != 0 && asked.Add(pid))
                    windows.Add((pid, ProcessImage.PathOf(pid, ref buffer)));
                return true;
            }, IntPtr.Zero);
            return Pick(windows, (uint)Environment.ProcessId);
        }

        /// <summary>The paths to offer from the windows found: the caller's
        /// own process and a process that gave no path are left out, a path
        /// shows once whatever its case, and the list is in file name
        /// order.</summary>
        internal static IReadOnlyList<string> Pick(IEnumerable<(uint Pid, string Path)> windows, uint ownPid)
            => windows.Where(w => w.Pid != ownPid && !string.IsNullOrEmpty(w.Path))
                      .Select(w => w.Path)
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                      .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                      .ToList();
    }
}
