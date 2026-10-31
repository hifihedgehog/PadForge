using System;
using System.Windows;
using System.Windows.Threading;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    /// <summary>
    /// The Keep prompt after a keyboard's switch to PadForge's driver
    /// (#549), or a mouse's. Windows stops typing with the keyboard, or
    /// moving the pointer with the mouse, at the switch, and a device on a
    /// shared receiver can take the other with it, so the switch asks to be
    /// kept and undoes itself when nobody answers: the way
    /// Windows' display settings undo a change after 15 seconds. Only the
    /// Keep button keeps it. Give It Back, Esc, the close button and the
    /// countdown's end all give the keyboard back.
    /// </summary>
    public partial class KeepDriverDialog : Wpf.Ui.Controls.FluentWindow
    {
        private readonly DispatcherTimer _timer;
        private int _remaining;
        private bool _kept;

        private KeepDriverDialog(string deviceName, int seconds, bool mouse)
        {
            InitializeComponent();
            // ConfirmDialog's remedy for a FluentWindow with no title bar,
            // so the prompt can be moved.
            MouseLeftButtonDown += (_, __) => { try { DragMove(); } catch { } };

            Title = mouse ? Strings.Instance.Devices_DriverKeepTitle_Mouse : Strings.Instance.Devices_DriverKeepTitle;
            TitleText.Text = Title;
            MessageText.Text = string.Format(mouse
                ? Strings.Instance.Devices_DriverKeepMessage_Mouse_Format
                : Strings.Instance.Devices_DriverKeepMessage_Format, deviceName);
            _remaining = Math.Max(1, seconds);
            ShowRemaining();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) =>
            {
                _remaining = CountDown(_remaining);
                if (_remaining <= 0)
                {
                    _timer.Stop();
                    Close();
                    return;
                }
                ShowRemaining();
            };
            Loaded += (_, _) =>
            {
                GiveBackButton.Focus();
                _timer.Start();
            };
            Closed += (_, _) => _timer.Stop();
        }

        /// <summary>Modal prompt, centered on <paramref name="owner"/>
        /// (screen when null), worded for a mouse with
        /// <paramref name="mouse"/>. True only when the user clicked Keep
        /// before the countdown ran out.</summary>
        public static bool Show(Window owner, string deviceName, int seconds, bool mouse = false)
        {
            var dlg = new KeepDriverDialog(deviceName, seconds, mouse);
            if (owner != null)
                dlg.Owner = owner;
            else
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dlg.ShowDialog();
            return dlg._kept;
        }

        /// <summary>One tick of the countdown: the seconds left after it,
        /// never below zero.</summary>
        internal static int CountDown(int remaining) => Math.Max(0, remaining - 1);

        private void ShowRemaining()
            => CountdownText.Text = string.Format(Strings.Instance.Devices_DriverKeepCountdown_Format, _remaining);

        private void KeepButton_Click(object sender, RoutedEventArgs e)
        {
            // Kept only while the countdown still runs: a click that lands
            // as it ends finds the window closing and changes nothing.
            if (_remaining <= 0) return;
            _timer.Stop();
            _kept = true;
            DialogResult = true;
        }
    }
}
