using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PadForge.Services
{
    /// <summary>
    /// The running copy's side of <see cref="RunningCopy"/> (#597): the two
    /// message-only windows a second copy of the exe looks for.
    ///
    /// <para>The signal window stands from the moment this copy holds the
    /// single-instance lock until the process ends. A second copy finds it by
    /// its name, reads this process's ID from it, and posts it one of two
    /// registered messages: the one that asks for the main window, or the
    /// one that asks this copy to close, as the tray icon's Exit does (#600
    /// item 44). That copy runs without administrator rights and this one has
    /// them, and Windows drops a message that crosses that line unless the
    /// receiving window lets it through, so the window lets those two
    /// messages through, and WM_COPYDATA, which it takes only while this copy
    /// waits at its start for the switches of the start that ran the logon
    /// task (<see cref="TakeStartSwitches"/>).</para>
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
        private const uint WM_COPYDATA = 0x004A;

        /// <summary>How long a copy started through the logon task waits for
        /// the switches of the start that ran it. That start hands them as
        /// soon as it finds the signal window, which stands before this
        /// wait.</summary>
        internal const int StartSwitchesWaitMs = 5000;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeInfo);

        private readonly string _signalName;
        private readonly string _readyName;
        private readonly string _showMessageName;
        private readonly string _exitMessageName;
        private HwndSource _signal;
        private HwndSource _ready;
        private uint _showMessage;
        private uint _exitMessage;
        private Action _show;
        private Action _exit;
        private bool _showOwed;
        private bool _exitOwed;
        private DispatcherFrame _switchesWait;
        private string[] _switches;

        /// <param name="signalName">The signal window's name. The app passes
        /// none and stands under the names every second copy looks for. A
        /// test passes names of its own.</param>
        /// <param name="readyName">The ready window's name.</param>
        /// <param name="showMessageName">The show message's name.</param>
        /// <param name="exitMessageName">The exit message's name.</param>
        public InstanceSignal(string signalName = null, string readyName = null, string showMessageName = null,
            string exitMessageName = null)
        {
            _signalName = string.IsNullOrEmpty(signalName) ? RunningCopy.SignalWindowName : signalName;
            _readyName = string.IsNullOrEmpty(readyName) ? RunningCopy.ReadyWindowName : readyName;
            _showMessageName = string.IsNullOrEmpty(showMessageName) ? RunningCopy.ShowMessageName : showMessageName;
            _exitMessageName = string.IsNullOrEmpty(exitMessageName) ? RunningCopy.ExitMessageName : exitMessageName;
        }

        /// <summary>Whether any of the four names is the app's own. Windows
        /// compares window names and registered message names without regard
        /// to case.</summary>
        private bool UsesTheAppsOwnNames
            => string.Equals(_signalName, RunningCopy.SignalWindowName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(_readyName, RunningCopy.ReadyWindowName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(_showMessageName, RunningCopy.ShowMessageName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(_exitMessageName, RunningCopy.ExitMessageName, StringComparison.OrdinalIgnoreCase);

        /// <summary>The signal window's handle, or zero before
        /// <see cref="Open"/> and after <see cref="Dispose"/>.</summary>
        internal IntPtr SignalWindow => _signal?.Handle ?? IntPtr.Zero;

        /// <summary>Whether the ready window stands.</summary>
        internal bool IsReady => _ready != null;

        /// <summary>The show message's number, or 0 before
        /// <see cref="Open"/>.</summary>
        internal uint ShowMessage => _showMessage;

        /// <summary>The exit message's number, or 0 before
        /// <see cref="Open"/>.</summary>
        internal uint ExitMessage => _exitMessage;

        /// <summary>Makes the signal window and lets the show and exit
        /// messages through. False when nothing was made.</summary>
        public bool Open()
        {
            // The test host stands no window where a second copy of a running
            // PadForge would find it and take the test host for that copy.
            if (RunningCopy.OffLimitsForTest && UsesTheAppsOwnNames) return false;
            if (_signal != null) return true;
            _showMessage = RunningCopy.RegisterWindowMessageW(_showMessageName);
            _exitMessage = RunningCopy.RegisterWindowMessageW(_exitMessageName);
            _signal = MessageOnlyWindow(_signalName);
            _signal.AddHook(OnMessage);
            // Without this a copy that has no administrator rights could post
            // the messages and Windows would drop them on the way here.
            if (_showMessage != 0)
                ChangeWindowMessageFilterEx(_signal.Handle, _showMessage, MSGFLT_ALLOW, IntPtr.Zero);
            if (_exitMessage != 0)
                ChangeWindowMessageFilterEx(_signal.Handle, _exitMessage, MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(_signal.Handle, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero);
            return true;
        }

        /// <summary>The main window exists: <paramref name="show"/> shows it
        /// and <paramref name="exit"/> closes PadForge. A request that came
        /// before this is honored now, a close after a show.</summary>
        public void Attach(Action show, Action exit)
        {
            _show = show;
            _exit = exit;
            if (_show != null && _showOwed)
            {
                _showOwed = false;
                _show();
            }
            if (_exit != null && _exitOwed)
            {
                _exitOwed = false;
                _exit();
            }
        }

        /// <summary>
        /// The start switches of a start without rights that ran the logon
        /// task (#600 item 44), when that start says they are coming
        /// (<see cref="RunningCopy.MarkStartPending"/>): waited for up to
        /// <paramref name="waitMs"/> while the thread's messages run. The
        /// task's command line is fixed, so they come over the signal window.
        /// Only the start set's words are kept
        /// (<see cref="PadForge.Common.CommandLineSwitches.StartSwitchesOnly"/>),
        /// so no other switch reaches this copy this way. They are taken once,
        /// during this wait alone: a copy past its start takes none, and any
        /// switch a running copy takes needs Automation.
        /// </summary>
        public IReadOnlyList<string> TakeStartSwitches(int waitMs)
        {
            if (_signal == null || _switchesWait != null || !RunningCopy.StartIsPending(_signalName)) return Array.Empty<string>();
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(Math.Max(0, waitMs)), DispatcherPriority.Send,
                (s, e) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            _switchesWait = frame;
            try
            {
                timer.Start();
                Dispatcher.PushFrame(frame);
            }
            finally
            {
                timer.Stop();
                _switchesWait = null;
            }
            string[] words = _switches;
            _switches = null;
            return PadForge.Common.CommandLineSwitches.StartSwitchesOnly(words ?? Array.Empty<string>());
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
            uint taken = unchecked((uint)message);
            if (_showMessage != 0 && taken == _showMessage)
            {
                handled = true;
                if (_show != null) _show();
                else _showOwed = true;
            }
            else if (_exitMessage != 0 && taken == _exitMessage)
            {
                handled = true;
                if (_exit != null) _exit();
                else _exitOwed = true;
            }
            else if (taken == WM_COPYDATA && _switchesWait != null && _switches == null
                && RunningCopy.ReadStartSwitches(lParam) is { } words)
            {
                handled = true;
                _switches = words;
                _switchesWait.Continue = false;
                return new IntPtr(1);
            }
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
