using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Threading;
using PadForge.Common;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    /// <summary>
    /// The Keep prompt after a keyboard's switch to PadForge's driver
    /// (#549), a mouse's, or a precision touchpad's switch to PadForge alone.
    /// Windows stops typing with the keyboard, or moving the pointer with the
    /// mouse or touchpad, at the switch, and a device on a
    /// shared receiver can take the other with it, so the switch asks to be
    /// kept and undoes itself when nobody answers: the way
    /// Windows' display settings undo a change after 15 seconds. Only the
    /// Keep button keeps it. Give It Back, Esc and the
    /// countdown's end all give the keyboard back.
    /// </summary>
    public partial class KeepDriverDialog : DialogWindow
    {
        private readonly DispatcherTimer _timer;
        private readonly int _seconds;
        private long _deadline;
        private int _remaining;
        private int _shown = -1;
        private bool _kept;

        private KeepDriverDialog(string deviceName, int seconds, bool mouse, bool touchpad)
        {
            InitializeComponent();

            Title = touchpad ? Strings.Instance.Devices_TouchpadKeepTitle
                : mouse ? Strings.Instance.Devices_DriverKeepTitle_Mouse : Strings.Instance.Devices_DriverKeepTitle;
            Subtitle = string.Format(touchpad
                ? Strings.Instance.Devices_TouchpadKeepMessage_Format
                : mouse
                ? Strings.Instance.Devices_DriverKeepMessage_Mouse_Format
                : Strings.Instance.Devices_DriverKeepMessage_Format, deviceName);
            _seconds = Math.Max(1, seconds);
            CountdownBar.Maximum = _seconds;
            _remaining = _seconds;
            ShowRemaining();

            // Each tick reads the time left against the deadline, so a tick
            // a busy UI thread delays never stretches the wait. Counting
            // ticks, a mouse's prompt stated 15 seconds and closed after 19
            // while the device pass registered its row.
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) =>
            {
                _remaining = RemainingAt(_deadline, Stopwatch.GetTimestamp());
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
                // The wait runs from the moment the prompt shows.
                _deadline = Stopwatch.GetTimestamp() + (long)_seconds * Stopwatch.Frequency;
                _timer.Start();
            };
            Closed += (_, _) => _timer.Stop();
        }

        /// <summary>Modal prompt, centered on <paramref name="owner"/>
        /// (screen when null), worded for a mouse with
        /// <paramref name="mouse"/> and for a touchpad with
        /// <paramref name="touchpad"/>. True only when the user clicked Keep
        /// before the countdown ran out.</summary>
        public static bool Show(Window owner, string deviceName, int seconds, bool mouse = false, bool touchpad = false)
        {
            var dlg = new KeepDriverDialog(deviceName, seconds, mouse, touchpad);
            if (owner != null)
                dlg.Owner = owner;
            else
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dlg.ShowDialog();
            return dlg._kept;
        }

        /// <summary>The whole seconds left until <paramref name="deadline"/>,
        /// rounded up and never below zero, both in Stopwatch ticks.</summary>
        internal static int RemainingAt(long deadline, long now)
            => now >= deadline ? 0 : (int)Math.Ceiling((deadline - now) / (double)Stopwatch.Frequency);

        /// <summary>The bar drains by the exact time left, at the timer's
        /// step. The sentence counts whole seconds, its number in the code
        /// face as the board draws it, and is made again only when the
        /// number changes, so a screen reader hears each second once.</summary>
        private void ShowRemaining()
        {
            CountdownBar.Value = _deadline == 0
                ? _seconds
                : Math.Max(0, (_deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
            if (_shown == _remaining) return;
            _shown = _remaining;

            string format = Strings.Instance.Devices_DriverKeepCountdown_Format;
            var (before, after) = FormatRuns.Split(format);
            var number = new Run(_remaining.ToString(CultureInfo.CurrentCulture));
            number.SetResourceReference(TextElement.FontFamilyProperty, "TelemetryFontFamily");
            CountdownText.Inlines.Clear();
            CountdownText.Inlines.Add(new Run(before));
            CountdownText.Inlines.Add(number);
            CountdownText.Inlines.Add(new Run(after));
            string sentence = string.Format(CultureInfo.CurrentCulture, format, _remaining);
            AutomationProperties.SetName(CountdownText, sentence);
            AutomationProperties.SetName(CountdownBar, sentence);
        }

        private void KeepButton_Click(object sender, RoutedEventArgs e)
        {
            // Kept only while the countdown still runs: a click that lands
            // after the time ran out changes nothing.
            if (RemainingAt(_deadline, Stopwatch.GetTimestamp()) <= 0) return;
            _timer.Stop();
            _kept = true;
            DialogResult = true;
        }
    }
}
