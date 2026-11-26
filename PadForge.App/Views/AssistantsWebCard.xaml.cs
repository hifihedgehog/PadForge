using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>The Assistants on the Web card (#600 item 11). While it shows
    /// and the armed window runs, it reads its view model again each second,
    /// so Accepting until ends on time. It reads it once each time it comes
    /// into view. Every other change reaches it through the door's Changed.
    ///
    /// <para>The Runtime Key box (#600 DESIGN-2 N4) never holds a kept key:
    /// while one is kept it shows a stand-in of masked characters. A key
    /// typed or pasted goes to the view model when the box is left or Enter
    /// is pressed, and the box is emptied of it at once. A box left empty
    /// forgets the kept key.</para></summary>
    public partial class AssistantsWebCard : UserControl
    {
        private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

        /// <summary>What the box shows while a key is kept: no key.</summary>
        internal static readonly string StandIn = new('k', 24);

        public AssistantsWebCard()
        {
            InitializeComponent();
            _clock.Tick += (_, _) => (DataContext as WebAccessViewModel)?.Refresh();
            // The window holds the card from its start, collapsed while
            // another page shows, and Loaded reaches it there too.
            Loaded += (_, _) => { FollowClock(); ShowKept(); };
            // A tunnel program installed or removed since the helper started is
            // seen when the list opens, and when the card shows (below).
            TunnelBox.DropDownOpened += (_, _) => (DataContext as WebAccessViewModel)?.LookForTunnels();
            Unloaded += (_, _) => _clock.Stop();
            IsVisibleChanged += (_, _) =>
            {
                // The addresses and the day's words change with no event, and a
                // tunnel program may have come or gone since the helper started.
                if (IsVisible)
                {
                    (DataContext as WebAccessViewModel)?.Refresh();
                    (DataContext as WebAccessViewModel)?.LookForTunnels();
                }
                FollowClock();
            };
            DataContextChanged += (_, e) =>
            {
                if (e.OldValue is WebAccessViewModel old) old.PropertyChanged -= OnModelChanged;
                if (e.NewValue is WebAccessViewModel now) now.PropertyChanged += OnModelChanged;
                ShowKept();
            };
            RuntimeKeyBox.LostFocus += (_, _) => Commit();
            // A click or a Tab into the box selects the stand-in, so a paste
            // takes its place whole.
            RuntimeKeyBox.GotKeyboardFocus += (_, _) => RuntimeKeyBox.SelectAll();
            RuntimeKeyBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Commit(); };
        }

        /// <summary>Whether the card's clock runs: while the card shows and
        /// the door accepts a new web assistant.</summary>
        internal static bool ClockRuns(bool visible, bool armed) => visible && armed;

        internal bool ClockRunning => _clock.IsEnabled;

        private void FollowClock()
        {
            bool run = ClockRuns(IsVisible, (DataContext as WebAccessViewModel)?.IsArmed == true);
            if (run && !_clock.IsEnabled) _clock.Start();
            else if (!run && _clock.IsEnabled) _clock.Stop();
        }

        /// <summary>The text the card itself last put in the Runtime Key
        /// box. A box that still holds it was not typed in.</summary>
        private string _shown = string.Empty;

        /// <summary>Whether the Runtime Key box holds the keyboard. A test
        /// gives its own.</summary>
        internal Func<bool> BoxHasKeyboard;

        private bool BoxIsFocused => BoxHasKeyboard?.Invoke() ?? RuntimeKeyBox.IsKeyboardFocusWithin;

        private void OnModelChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WebAccessViewModel.HasRuntimeKey) && !BoxIsFocused) ShowKept();
            else if (e.PropertyName == nameof(WebAccessViewModel.IsArmed)) FollowClock();
        }

        private void ShowKept()
        {
            bool kept = (DataContext as WebAccessViewModel)?.HasRuntimeKey == true;
            string want = kept ? StandIn : string.Empty;
            if (RuntimeKeyBox.Password != want) RuntimeKeyBox.Password = want;
            _shown = want;
        }

        internal void Commit()
        {
            if (DataContext is not WebAccessViewModel model) return;
            // A box that holds what the card put there was not typed in:
            // emptied by the card after a key went to the helper, it may
            // meet the kept key's answer while it still holds the keyboard.
            if (RuntimeKeyBox.Password == _shown) { ShowKept(); return; }
            string key = Committed(RuntimeKeyBox.Password, model.HasRuntimeKey);
            if (key == null) return;
            RuntimeKeyBox.Password = string.Empty;
            _shown = string.Empty;
            model.SetRuntimeKey(key);
            ShowKept();
        }

        /// <summary>What the box hands on when it is left: null for no
        /// change (the stand-in, or an empty box with no key kept), an empty
        /// text to forget the kept key, else the key, the stand-in taken off
        /// should a key have been typed after it.</summary>
        internal static string Committed(string typed, bool kept)
        {
            typed ??= string.Empty;
            if (typed == StandIn) return null;
            if (typed.Length == 0) return kept ? string.Empty : null;
            if (kept && typed.StartsWith(StandIn, StringComparison.Ordinal)) typed = typed.Substring(StandIn.Length);
            if (kept && typed.EndsWith(StandIn, StringComparison.Ordinal)) typed = typed.Substring(0, typed.Length - StandIn.Length);
            return typed.Length == 0 ? null : typed;
        }
    }
}
