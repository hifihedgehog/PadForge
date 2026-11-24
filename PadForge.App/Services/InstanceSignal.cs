using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PadForge.Services
{
    /// <summary>
    /// The running copy's side of <see cref="RunningCopy"/> (#597): the two
    /// message-only windows a second copy of the exe looks for.
    ///
    /// <para>The signal window stands from the moment this copy holds the
    /// single-instance lock until the process ends. A second copy finds it by
    /// its name, reads this process's ID from it, and posts it the one
    /// registered message that asks for the main window. That copy runs
    /// without administrator rights and this one has them, and Windows drops a
    /// message that crosses that line unless the receiving window lets it
    /// through, so the window lets that one message through and no other.</para>
    ///
    /// <para>The ready window stands while this copy is past its start and not
    /// yet closing. It takes no message. A second copy that finds no control
    /// pipe waits for it before it says Automation is off, and takes its
    /// absence at the end as the copy closing.</para>
    ///
    /// <para>Made, used and disposed on the interface's thread.</para>
    /// </summary>
    internal sealed class InstanceSignal : IDisposable
    {
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
        private const uint MSGFLT_ALLOW = 1;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeInfo);

        private readonly string _signalName;
        private readonly string _readyName;
        private readonly string _showMessageName;
        private HwndSource _signal;
        private HwndSource _ready;
        private uint _showMessage;
        private Action _show;
        private bool _showOwed;

        /// <param name="signalName">The signal window's name. The app passes
        /// none and stands under the names every second copy looks for. A
        /// test passes names of its own.</param>
        /// <param name="readyName">The ready window's name.</param>
        /// <param name="showMessageName">The registered message's name.</param>
        public InstanceSignal(string signalName = null, string readyName = null, string showMessageName = null)
        {
            _signalName = string.IsNullOrEmpty(signalName) ? RunningCopy.SignalWindowName : signalName;
            _readyName = string.IsNullOrEmpty(readyName) ? RunningCopy.ReadyWindowName : readyName;
            _showMessageName = string.IsNullOrEmpty(showMessageName) ? RunningCopy.ShowMessageName : showMessageName;
        }

        /// <summary>Whether any of the three names is the app's own. Windows
        /// compares window names and registered message names without regard
        /// to case.</summary>
        private bool UsesTheAppsOwnNames
            => string.Equals(_signalName, RunningCopy.SignalWindowName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(_readyName, RunningCopy.ReadyWindowName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(_showMessageName, RunningCopy.ShowMessageName, StringComparison.OrdinalIgnoreCase);

        /// <summary>The signal window's handle, or zero before
        /// <see cref="Open"/> and after <see cref="Dispose"/>.</summary>
        internal IntPtr SignalWindow => _signal?.Handle ?? IntPtr.Zero;

        /// <summary>Whether the ready window stands.</summary>
        internal bool IsReady => _ready != null;

        /// <summary>The registered message's number, or 0 before
        /// <see cref="Open"/>.</summary>
        internal uint ShowMessage => _showMessage;

        /// <summary>Makes the signal window and lets the show message
        /// through. False when nothing was made.</summary>
        public bool Open()
        {
            // The test host stands no window where a second copy of a running
            // PadForge would find it and take the test host for that copy.
            if (RunningCopy.OffLimitsForTest && UsesTheAppsOwnNames) return false;
            if (_signal != null) return true;
            _showMessage = RunningCopy.RegisterWindowMessageW(_showMessageName);
            _signal = MessageOnlyWindow(_signalName);
            _signal.AddHook(OnMessage);
            // Without this a copy that has no administrator rights could post
            // the message and Windows would drop it on the way here.
            if (_showMessage != 0)
                ChangeWindowMessageFilterEx(_signal.Handle, _showMessage, MSGFLT_ALLOW, IntPtr.Zero);
            return true;
        }

        /// <summary>The main window exists, and <paramref name="show"/> shows
        /// it. A request that came before this is honored now.</summary>
        public void Attach(Action show)
        {
            _show = show;
            if (_show == null || !_showOwed) return;
            _showOwed = false;
            _show();
        }

        /// <summary>This copy is past its start. Nothing is made before
        /// <see cref="Open"/> made the signal window.</summary>
        public void MarkReady()
        {
            if (_signal == null || _ready != null) return;
            _ready = MessageOnlyWindow(_readyName);
        }

        /// <summary>This copy is closing: the ready window goes, and the
        /// signal window stays to the end, so a copy that waits for this
        /// one's exit still finds its process.</summary>
        public void MarkClosing()
        {
            _ready?.Dispose();
            _ready = null;
        }

        private static HwndSource MessageOnlyWindow(string name)
            => new HwndSource(new HwndSourceParameters(name)
            {
                ParentWindow = HWND_MESSAGE,
                WindowStyle = 0,
                Width = 0,
                Height = 0,
            });

        private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_showMessage == 0 || unchecked((uint)message) != _showMessage) return IntPtr.Zero;
            handled = true;
            if (_show != null) _show();
            else _showOwed = true;
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            MarkClosing();
            if (_signal == null) return;
            _signal.RemoveHook(OnMessage);
            _signal.Dispose();
            _signal = null;
        }
    }
}
