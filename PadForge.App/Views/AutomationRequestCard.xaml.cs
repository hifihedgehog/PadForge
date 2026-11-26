using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PadForge.Resources.Strings;
using PadForge.Services.Automation;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>
    /// The request card (#600): asks the user to allow or refuse what a
    /// program asked, above other windows, without taking the focus. It shows
    /// the oldest question that waits, "1 of 3" while several do, and the
    /// time left, and moves on as each ends. The questions are the Automation
    /// page's own list (DashboardViewModel.AutomationRequests), so an answer
    /// on the page closes the card's question and an answer here takes it
    /// off the page.
    ///
    /// <para>The window recipe is the On-Screen Keyboard's: never activated,
    /// a tool window, and clicks taken. It has no owner, so it neither raises
    /// PadForge's window nor hides with it. While a modal dialog of PadForge
    /// is open the card stays hidden, so it never draws over one, and it
    /// comes back with the time left once the dialog closes.</para>
    /// </summary>
    public partial class AutomationRequestCard : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_NOACTIVATE = 3;
        private const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;
        private const int MDT_EFFECTIVE_DPI = 0;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

        /// <summary>Room between the card and the work area's corner, past
        /// the shadow's margin, in DIPs.</summary>
        internal const double CornerGap = 8;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        private readonly ObservableCollection<AutomationRequestItem> _requests;
        private readonly Func<IntPtr> _anchor;
        private readonly Func<DateTimeOffset> _now;
        private readonly DispatcherTimer _clock;
        private bool _closed;

        /// <param name="requests">The questions that wait, oldest first.</param>
        /// <param name="anchor">PadForge's main window, whose display the card
        /// stands on and whose being disabled says a modal dialog is open.
        /// Null for a card that is only drawn, which never shows or places
        /// itself.</param>
        /// <param name="now">The clock the time left is read on.</param>
        internal AutomationRequestCard(ObservableCollection<AutomationRequestItem> requests, Func<IntPtr> anchor,
            Func<DateTimeOffset> now = null)
        {
            _requests = requests ?? throw new ArgumentNullException(nameof(requests));
            _anchor = anchor;
            _now = now ?? (() => DateTimeOffset.Now);
            // The shared confirm's two button looks.
            Resources.MergedDictionaries.Add(DialogWindow.Looks);
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += (_, _) => Place();
            DpiChanged += (_, _) => Place();
            _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clock.Tick += (_, _) => Refresh();
            _requests.CollectionChanged += OnRequestsChanged;
            if (_anchor != null)
            {
                System.Windows.Interop.ComponentDispatcher.EnterThreadModal += OnThreadModalChanged;
                System.Windows.Interop.ComponentDispatcher.LeaveThreadModal += OnThreadModalChanged;
            }
            Closed += (_, _) =>
            {
                _closed = true;
                _clock.Stop();
                _requests.CollectionChanged -= OnRequestsChanged;
                if (_anchor != null)
                {
                    System.Windows.Interop.ComponentDispatcher.EnterThreadModal -= OnThreadModalChanged;
                    System.Windows.Interop.ComponentDispatcher.LeaveThreadModal -= OnThreadModalChanged;
                }
            };
            Refresh();
        }

        /// <summary>The question on the card, or null while none waits.</summary>
        internal AutomationRequestItem Shown => DataContext as AutomationRequestItem;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            Place();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEACTIVATE) { handled = true; return (IntPtr)MA_NOACTIVATE; }
            return IntPtr.Zero;
        }

        private void OnRequestsChanged(object sender, NotifyCollectionChangedEventArgs e) => Refresh();

        private void OnThreadModalChanged(object sender, EventArgs e) => Refresh();

        /// <summary>Shows the oldest question, its place in the queue and
        /// its time left, and shows or hides the card.</summary>
        internal void Refresh()
        {
            if (_closed) return;
            var item = _requests.Count > 0 ? _requests[0] : null;
            if (!ReferenceEquals(DataContext, item)) DataContext = item;
            if (item == null)
            {
                _clock.Stop();
                PositionText.Text = string.Empty;
                TimeText.Text = string.Empty;
                if (_anchor != null && IsVisible) Hide();
                return;
            }
            WordAnswers(KindOf?.Invoke(item.Id) ?? ApprovalKind.Change);
            PositionText.Text = _requests.Count > 1
                ? string.Format(CultureInfo.CurrentCulture, Strings.Instance.Automation_Request_Position_Format, 1, _requests.Count)
                : string.Empty;
            TimeText.Text = TimeLeftLine(item.Expires - _now());
            if (_anchor == null) return;
            if (ADialogIsOpen())
            {
                // The time runs on, and the card comes back when the dialog
                // closes (LeaveThreadModal, or the next tick).
                if (IsVisible) Hide();
                _clock.Start();
                return;
            }
            if (!IsVisible)
            {
                Show();
                Place();
            }
            _clock.Start();
        }

        /// <summary>The kind of a waiting question by its ID (#600), set by
        /// the window that owns the questions. Null words every question as
        /// a change.</summary>
        internal static Func<int, ApprovalKind> KindOf { get; set; }

        /// <summary>The two answers in the words and looks of the question's
        /// kind (#600): a change's Don't Allow and an ember Allow; a
        /// program's first connection with both alike, since Allow lets it
        /// read every key; and a show's Not Now and Show, both alike.</summary>
        internal void WordAnswers(ApprovalKind kind)
        {
            DenyButton.Content = AutomationRequestItem.DenyWordsFor(kind);
            AllowButton.Content = AutomationRequestItem.AllowWordsFor(kind);
            AllowButton.Style = (Style)FindResource(kind == ApprovalKind.Change ? "DialogPrimaryButton" : "DialogButton");
        }

        /// <summary>The line under the details: what no answer does, and
        /// when.</summary>
        internal static string TimeLeftLine(TimeSpan left)
        {
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            // Whole seconds, rounded up, so the line never reads 0:00 while
            // the question still waits.
            long seconds = (long)Math.Ceiling(left.TotalSeconds);
            string time = (seconds / 60).ToString(CultureInfo.CurrentCulture) + ":" + (seconds % 60).ToString("00", CultureInfo.CurrentCulture);
            return string.Format(CultureInfo.CurrentCulture, Strings.Instance.Automation_Request_TimeLeft_Format, time);
        }

        /// <summary>Whether a modal dialog of PadForge is open: WPF's own
        /// (ShowDialog), or one of Windows' that disables the main window
        /// while it is open (a file dialog, a message box).</summary>
        private bool ADialogIsOpen()
        {
            if (System.Windows.Interop.ComponentDispatcher.IsThreadModal) return true;
            IntPtr main = _anchor?.Invoke() ?? IntPtr.Zero;
            return main != IntPtr.Zero && !IsWindowEnabled(main);
        }

        /// <summary>Puts the card's corner at the bottom right of the work
        /// area of the display PadForge's window is on, in that display's
        /// pixels: the primary display while that window has never been
        /// shown (a start in the tray). A card that moves to a display of
        /// another scale is sized again by WPF, and comes back here
        /// (SizeChanged).</summary>
        private void Place()
        {
            if (_anchor == null || !IsVisible) return;
            IntPtr main = _anchor();
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            IntPtr monitor = main != IntPtr.Zero
                ? MonitorFromWindow(main, MONITOR_DEFAULTTONEAREST)
                : MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
            double scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
            var (x, y) = Corner(info.rcWork.Right, info.rcWork.Bottom, scale, ActualWidth, ActualHeight);
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        /// <summary>Where the card's top left goes, in a display's pixels:
        /// its size and <see cref="CornerGap"/> in from the work area's
        /// right and bottom edges, at the display's scale.</summary>
        internal static (int X, int Y) Corner(int workRight, int workBottom, double scale, double width, double height)
        {
            int w = (int)Math.Ceiling(width * scale), h = (int)Math.Ceiling(height * scale);
            int gap = (int)Math.Round(CornerGap * scale);
            return (workRight - gap - w, workBottom - gap - h);
        }
    }
}
