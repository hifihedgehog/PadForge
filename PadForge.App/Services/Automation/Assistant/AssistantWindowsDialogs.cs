using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// The Windows dialogs PadForge opens and cannot read (#600): a file
    /// chooser (IFileDialog) or a message box. Both are top-level windows of
    /// the dialog class <c>#32770</c> on the interface's thread, which no
    /// catalog markup describes. A file chooser holds the shell's view,
    /// whose window class is <c>DUIViewWndClassName</c>, and a message box
    /// holds none. An assistant is told one waits for the person, by its kind and
    /// title, and that what it asked carries on once the person answers.
    /// </summary>
    internal static class AssistantWindowsDialogs
    {
        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumThreadWindows(uint threadId, EnumProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        internal const string DialogClass = "#32770";
        internal const string ShellViewClass = "DUIViewWndClassName";

        private static string ClassOf(IntPtr hwnd)
        {
            var name = new StringBuilder(256);
            return GetClassNameW(hwnd, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
        }

        /// <summary>A Windows dialog of the main window's thread that waits
        /// for the person, in a sentence, or null when none does.</summary>
        internal static string Waiting(Window main)
        {
            if (main == null) return null;
            IntPtr hwnd = new WindowInteropHelper(main).Handle;
            if (hwnd == IntPtr.Zero) return null;
            uint thread = GetWindowThreadProcessId(hwnd, out _);
            string found = null;
            EnumThreadWindows(thread, (w, _) =>
            {
                if (!IsWindowVisible(w) || ClassOf(w) != DialogClass) return true;
                bool chooser = false;
                EnumChildWindows(w, (c, _) =>
                {
                    if (ClassOf(c) == ShellViewClass) { chooser = true; return false; }
                    return true;
                }, IntPtr.Zero);
                var title = new StringBuilder(256);
                GetWindowTextW(w, title, title.Capacity);
                found = Sentence(chooser, AutomationReadScrub.Text(title.ToString()));
                return false;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>The words an assistant reads for a Windows dialog.</summary>
        internal static string Sentence(bool fileChooser, string title)
            => (fileChooser ? "A Windows file chooser" : "A Windows message box")
               + (string.IsNullOrWhiteSpace(title) ? string.Empty : ", \"" + title.Trim() + "\",")
               + " waits for the person. PadForge cannot read it, and what was asked carries on once the person answers it.";
    }
}
