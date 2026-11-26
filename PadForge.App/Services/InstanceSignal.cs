using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PadForge.Services
{
    /// <summary>
    /// The running copy's side of <see cref="RunningCopy"/> (#600): the two
    /// message-only windows a second copy of the exe looks for, and the
    /// signal pipe that takes what a second copy asks.
    ///
    /// <para>The signal window stands from the moment this copy holds the
    /// single-instance lock until the process ends. A second copy finds it by
    /// its name and reads this process's ID from it. What it asks goes over
    /// the signal pipe of the same name, which stands as long as the window:
    /// show the main window, close as the tray icon's Exit does (#600 item
    /// 44), or, while this copy waits at its start, the switches of the start
    /// that ran the logon task (<see cref="TakeStartSwitches"/>). A posted
    /// message carries no sender a window can check, so the window lets no
    /// message through from a process of lower integrity than this copy's,
    /// as Windows does by default. The pipe reads its client's token, as the
    /// control pipe does (<see cref="ExternalControlService.Judge"/>): the
    /// desktop's user or PadForge's, in this Windows session, at medium
    /// integrity or above, and no app container. So the user's own second
    /// copy is taken, and a sandboxed or low-integrity program is
    /// not.</para>
    ///
    /// <para>The ready window stands while this copy is past its start and not
    /// yet closing. It takes no message. A second copy that finds no control
    /// pipe waits for it before it says Automation is off, and takes its
    /// absence at the end as the copy closing.</para>
    ///
    /// <para>The windows are made, used and disposed on the interface's
    /// thread. The pipe's lines run there too.</para>
    /// </summary>
    internal sealed class InstanceSignal : IDisposable
    {
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        /// <summary>How long a copy started through the logon task waits for
        /// the switches of the start that ran it. That start hands them as
        /// soon as it finds the signal window, which stands before this
        /// wait.</summary>
        internal const int StartSwitchesWaitMs = 5000;

        private readonly string _signalName;
        private readonly string _readyName;
        private readonly string _showMessageName;
        private readonly string _exitMessageName;
        private HwndSource _signal;
        private HwndSource _ready;
        private ExternalControlService _pipe;
        private Dispatcher _dispatcher;
        private uint _showMessage;
        private uint _exitMessage;
        private Action _show;
        private Action _exit;
        private bool _showOwed;
        private bool _exitOwed;
        private bool _closing;
        private DispatcherFrame _switchesWait;
        private string[] _switches;

        /// <param name="signalName">The signal window's name, which its pipe
        /// takes too. The app passes none and stands under the names every
        /// second copy looks for. A test passes names of its own.</param>
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

        /// <summary>Test seam: reads the signal pipe's client's token, as
        /// <see cref="ExternalControlService.ReadToken"/> does, set before
        /// <see cref="Open"/>. Null in the app.</summary>
        internal Func<NamedPipeServerStream, Automation.AutomationClientToken?> ReadToken { get; set; }

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

        /// <summary>Whether the signal pipe serves.</summary>
        internal bool PipeServes => _pipe?.State == ExternalControlService.PipeState.Serving;

        /// <summary>The show message's number, or 0 before
        /// <see cref="Open"/>.</summary>
        internal uint ShowMessage => _showMessage;

        /// <summary>The exit message's number, or 0 before
        /// <see cref="Open"/>.</summary>
        internal uint ExitMessage => _exitMessage;

        /// <summary>Starts the signal pipe and makes the signal window. False
        /// when nothing was made. A pipe whose name another program took
        /// first does not serve (<see cref="PipeServes"/>), as the control
        /// pipe does not then, and a second copy without rights then reaches
        /// no carrier: it takes the pipe only from Administrators or the
        /// system, so nothing is sent to that program.</summary>
        public bool Open()
        {
            // The test host stands no window or pipe where a second copy of a
            // running PadForge would find it and take the test host for that
            // copy.
            if (RunningCopy.OffLimitsForTest && UsesTheAppsOwnNames) return false;
            if (_signal != null) return true;
            _dispatcher = Dispatcher.CurrentDispatcher;
            _showMessage = RunningCopy.RegisterWindowMessageW(_showMessageName);
            _exitMessage = RunningCopy.RegisterWindowMessageW(_exitMessageName);
            // The pipe stands before the window: a start that finds the
            // window asks over the pipe at once (StartByTask).
            _pipe = new ExternalControlService(Answer, RunningCopy.SignalPipeName(_signalName));
            if (ReadToken != null) _pipe.ReadToken = ReadToken;
            _pipe.Start();
            _signal = MessageOnlyWindow(_signalName);
            _signal.AddHook(OnMessage);
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
        /// task's command line is fixed, so they come over the signal pipe.
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
        /// signal window and its pipe stay to the end, so a copy that waits
        /// for this one's exit still finds its process. From here a show is
        /// taken and dropped, and one that waited for the window is
        /// forgotten: a window in its shutdown is never shown again, and on
        /// 2026-11-26 a second start's show reached one after its tray icon
        /// was gone.</summary>
        public void MarkClosing()
        {
            _closing = true;
            _showOwed = false;
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

        /// <summary>A line of the signal pipe, on the pipe's thread: refused
        /// for a client the token checks did not take, and otherwise run on
        /// the interface's thread.</summary>
        private ExternalControlService.Answer Answer(ExternalControlService.Request request)
        {
            if (request.Refusal != null) return new ExternalControlService.Answer(ExternalControlService.Refused);
            string line = request.Line ?? string.Empty;
            if (line == RunningCopy.ShowLine) return Later(TakeShow);
            if (line == RunningCopy.ExitLine) return Later(TakeExit);
            if (line.StartsWith(RunningCopy.SwitchesLine + " ", StringComparison.Ordinal))
            {
                string[] words = RunningCopy.ReadStartSwitches(line.Substring(RunningCopy.SwitchesLine.Length + 1));
                if (words == null) return new ExternalControlService.Answer("error unreadable");
                bool taken;
                try
                {
                    taken = _dispatcher.Invoke(() => TakeSwitches(words), DispatcherPriority.Send, request.Stopping,
                        TimeSpan.FromMilliseconds(StartSwitchesWaitMs));
                }
                catch
                {
                    taken = false;
                }
                return new ExternalControlService.Answer(taken ? "ok" : "error not-waiting");
            }
            return new ExternalControlService.Answer("error unknown-line");
        }

        /// <summary>Runs <paramref name="action"/> on the interface's thread
        /// once its turn comes, and answers ok now.</summary>
        private ExternalControlService.Answer Later(Action action)
        {
            try { _dispatcher.BeginInvoke(action); }
            catch { return new ExternalControlService.Answer("error closing"); }
            return new ExternalControlService.Answer("ok");
        }

        private void TakeShow()
        {
            if (_closing) return;
            if (_show != null) _show();
            else _showOwed = true;
        }

        private void TakeExit()
        {
            if (_exit != null) _exit();
            else _exitOwed = true;
        }

        private bool TakeSwitches(string[] words)
        {
            if (_switchesWait == null || _switches != null) return false;
            _switches = words;
            _switchesWait.Continue = false;
            return true;
        }

        /// <summary>The show and exit messages, from a process that runs at
        /// this copy's integrity or above, the only one Windows lets post to
        /// this window.</summary>
        private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            uint taken = unchecked((uint)message);
            if (_showMessage != 0 && taken == _showMessage)
            {
                handled = true;
                TakeShow();
            }
            else if (_exitMessage != 0 && taken == _exitMessage)
            {
                handled = true;
                TakeExit();
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            MarkClosing();
            if (_pipe != null)
            {
                _pipe.Dispose();
                _pipe = null;
            }
            if (_signal == null) return;
            _signal.RemoveHook(OnMessage);
            _signal.Dispose();
            _signal = null;
        }
    }
}
