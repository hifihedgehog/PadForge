using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace PadForge.Views
{
    /// <summary>
    /// A file dropped from Explorer on the Plugins page while PadForge runs
    /// with rights the desktop shell lacks (#600 item 18). Windows keeps a
    /// program without rights from reaching one with rights by its window
    /// messages (UIPI), and the OLE drop WPF registers on every window
    /// (HwndSource, DragDrop.RegisterDropTarget) is one of the things it
    /// keeps out, so Explorer's drag never arrives. A window that takes the
    /// shell's older drop, WM_DROPFILES, and lets that message and the two
    /// that carry its data through its filter gets the file (GLFW,
    /// win32_window.c, ChangeWindowMessageFilterEx for WM_DROPFILES,
    /// WM_COPYDATA and WM_COPYGLOBALDATA and DragAcceptFiles, a window with
    /// no OLE drop of its own).
    ///
    /// <para>So while the Plugins page shows in such a copy, the window's
    /// OLE drop is taken off, the window takes WM_DROPFILES, and the one file
    /// a drop names goes to the confirmation Add a Plugin… shows. When the
    /// page goes, the filter is closed again and WPF's own drop comes back,
    /// which the Devices page's drag between its cards uses. A copy without
    /// such rights keeps WPF's drop throughout, which takes Explorer's drag
    /// as it is (PluginsPage_Drop).</para>
    /// </summary>
    public partial class ExtrasPage
    {
        internal const int WM_DROPFILES = 0x0233, WM_COPYDATA = 0x004A, WM_COPYGLOBALDATA = 0x0049;
        private const uint MSGFLT_ALLOW = 1, MSGFLT_DISALLOW = 2;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr filterStatus);

        [DllImport("shell32.dll")]
        private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFileW(IntPtr drop, uint file, StringBuilder name, uint size);

        [DllImport("shell32.dll")]
        private static extern void DragFinish(IntPtr drop);

        [DllImport("ole32.dll")]
        private static extern int RevokeDragDrop(IntPtr hwnd);

        [DllImport("kernel32.dll")]
        private static extern UIntPtr GlobalSize(IntPtr memory);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr memory);

        /// <summary>The size of DROPFILES (shlobj_core.h): pFiles, pt, fNC,
        /// fWide.</summary>
        internal const int DropFilesSize = 20;

        /// <summary>
        /// Whether a WM_DROPFILES handle holds what the shell's drop holds: a
        /// DROPFILES header whose list starts right after it, in UTF-16, and
        /// ends with its two nulls inside the block. The filter lets any
        /// program without rights post WM_DROPFILES with a value of its
        /// choosing, and DragFinish frees the handle it is given, so a value
        /// that names some other block of this process is never read as a
        /// drop nor freed. Microsoft's archived post "Q: Why doesn't drag and
        /// drop work when my application is running elevated?" calls opening
        /// these messages no safe way to take a drag: this check keeps that
        /// door to the one handle shape.
        /// </summary>
        internal static bool LooksLikeShellDrop(IntPtr drop)
        {
            if (drop == IntPtr.Zero) return false;
            long size = (long)GlobalSize(drop).ToUInt64();
            if (size < DropFilesSize + 4) return false;
            IntPtr at = GlobalLock(drop);
            if (at == IntPtr.Zero) return false;
            try
            {
                if (Marshal.ReadInt32(at, 0) != DropFilesSize || Marshal.ReadInt32(at, 16) == 0) return false;
                // The names, each ended by a null, and one more null after
                // the last.
                long offset = DropFilesSize;
                bool any = false;
                while (offset + 2 <= size)
                {
                    long start = offset;
                    while (offset + 2 <= size && Marshal.ReadInt16(at, (int)offset) != 0) offset += 2;
                    if (offset + 2 > size) return false;
                    if (offset == start) return any;
                    any = true;
                    offset += 2;
                }
                return false;
            }
            finally
            {
                GlobalUnlock(drop);
            }
        }

        /// <summary>The messages the window lets through while it takes the
        /// shell's drop.</summary>
        internal static readonly int[] ShellDropMessages = { WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA };

        private HwndSource _shellDropSource;

        /// <summary>Takes the shell's drop while the Plugins page shows in a
        /// copy with rights the shell lacks, and gives it back otherwise.</summary>
        private void FollowShellDrop()
        {
            bool want = IsVisible && CurrentExtra == "Plugins" && PadForge.Services.UserSave.RunsWithRightsTheShellLacks();
            if (want == (_shellDropSource != null)) return;
            if (want)
            {
                if (PresentationSource.FromVisual(this) is not HwndSource source || source.Handle == IntPtr.Zero) return;
                // WPF's OLE drop comes back by its own call when the page goes.
                // A WPF without that call keeps its drop, and the page as it is.
                if (RegisterWpfDrop == null) return;
                RevokeDragDrop(source.Handle);
                foreach (int message in ShellDropMessages) ChangeWindowMessageFilterEx(source.Handle, (uint)message, MSGFLT_ALLOW, IntPtr.Zero);
                DragAcceptFiles(source.Handle, true);
                source.AddHook(ShellDropHook);
                _shellDropSource = source;
            }
            else
            {
                var source = _shellDropSource;
                _shellDropSource = null;
                source.RemoveHook(ShellDropHook);
                if (source.IsDisposed || source.Handle == IntPtr.Zero) return;
                DragAcceptFiles(source.Handle, false);
                foreach (int message in ShellDropMessages) ChangeWindowMessageFilterEx(source.Handle, (uint)message, MSGFLT_DISALLOW, IntPtr.Zero);
                RegisterWpfDrop.Invoke(null, new object[] { source.Handle });
            }
        }

        /// <summary>WPF's own registration of a window's OLE drop
        /// (DragDrop.RegisterDropTarget, which HwndSource calls for each
        /// window it makes), or null when this WPF has none.</summary>
        internal static readonly MethodInfo RegisterWpfDrop = typeof(DragDrop).GetMethod("RegisterDropTarget",
            BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(IntPtr) }, null);

        private IntPtr ShellDropHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != WM_DROPFILES) return IntPtr.Zero;
            handled = true;
            if (!LooksLikeShellDrop(wParam)) return IntPtr.Zero;
            string path = null;
            try
            {
                if (DragQueryFileW(wParam, uint.MaxValue, null, 0) == 1)
                {
                    uint length = DragQueryFileW(wParam, 0, null, 0);
                    var name = new StringBuilder((int)length + 1);
                    if (DragQueryFileW(wParam, 0, name, (uint)name.Capacity) == length) path = name.ToString();
                }
            }
            finally
            {
                DragFinish(wParam);
            }
            // A message from a program without rights: the file goes to the
            // confirmation alone, as a drop on the page does.
            if (path != null && System.IO.File.Exists(path) && !_describingPlugin
                && DataContext is ViewModels.DashboardViewModel { PluginActions: not null })
                AddPluginFile(path);
            return IntPtr.Zero;
        }
    }
}
