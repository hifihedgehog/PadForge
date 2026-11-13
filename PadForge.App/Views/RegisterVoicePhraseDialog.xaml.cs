using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Input;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using PadForge.Services;

namespace PadForge.Views
{
    /// <summary>
    /// Voice macro management (issue #317): a live "heard" readout naming the
    /// microphone that heard it, the type-and-name phrase registration flow
    /// with a Remove list, and under More the settings (enable, listening
    /// mode, confidence floor). Modeled on <see cref="RegisterNfcTagDialog"/>.
    /// There is no source picker: phrases live on the devices that carry the
    /// microphones, and every reachable microphone runs its own session.
    /// </summary>
    public partial class RegisterVoicePhraseDialog : DialogWindow
    {
        /// <summary>The confidence floor a setup starts with, and the value
        /// the row's reset gives.</summary>
        private const double UsualConfidence = 0.80;

        /// <summary>Row item for the phrase list: the registry entry plus a
        /// short-lived highlight so a recognized phrase lights its row with
        /// no mapping required.</summary>
        public sealed class PhraseRow : System.ComponentModel.INotifyPropertyChanged
        {
            /// <summary>The row's flash timer, restarted per recognition so
            /// overlapping hits extend the light instead of truncating it.</summary>
            public System.Windows.Threading.DispatcherTimer FlashTimer;
            public string Phrase { get; init; }
            public string Name { get; init; }
            private bool _isActive;
            public bool IsActive
            {
                get => _isActive;
                set
                {
                    if (_isActive == value) return;
                    _isActive = value;
                    PropertyChanged?.Invoke(this,
                        new System.ComponentModel.PropertyChangedEventArgs(nameof(IsActive)));
                }
            }
            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

            public override string ToString() => Phrase;
        }

        private readonly System.Collections.ObjectModel.ObservableCollection<PhraseRow> _rows = new();

        private Action<string, string, float, bool> _heardHandler;
        private bool _loading = true;
        private bool _listening;

        public RegisterVoicePhraseDialog()
        {
            InitializeComponent();

            _listening = VoiceMacroService.Enabled;
            EnabledBox.IsChecked = _listening;
            ModeBox.SelectedIndex = VoiceMacroService.ListeningMode == 1 ? 1 : 0;
            ConfidenceSlider.Value = Math.Clamp(VoiceMacroService.MinConfidence, 0.5, 0.99);
            ConfidenceText.Text = VoiceMacroService.MinConfidence.ToString("F2", CultureInfo.CurrentCulture);
            _loading = false;

            ShowWaiting();
            // More opens on a setting that is off its usual value, so the
            // switch that turns listening on is in view while it is off.
            VoiceMore.IsExpanded = !_listening
                || ModeBox.SelectedIndex == 1
                || Math.Abs(ConfidenceSlider.Value - UsualConfidence) > 0.004;

            RefreshList();

            // Static event: no instance to race, no dead subscription when
            // the dialog opens before the service or across its restart.
            _heardHandler = OnPhraseHeard;
            VoiceMacroService.PhraseHeard += _heardHandler;

            Loaded += (_, _) => PhraseBox.Focus();
            Closed += (s, e) => Unsubscribe();
        }

        private void Unsubscribe()
        {
            foreach (var row in _rows)
                try { row.FlashTimer?.Stop(); } catch { }
            if (_heardHandler != null)
            {
                try { VoiceMacroService.PhraseHeard -= _heardHandler; } catch { }
            }
            _heardHandler = null;
        }

        /// <summary>The line before anything is heard: that it waits for
        /// speech, or that listening is off and where to turn it on.</summary>
        private void ShowWaiting()
        {
            ShowLine(_listening ? Strings.Instance.Voice_ListeningIdle : Strings.Instance.Voice_ListeningOff);
        }

        /// <summary>A plain sentence on the live line, with no confidence
        /// beside it.</summary>
        private void ShowLine(string text)
        {
            HeardText.Inlines.Clear();
            HeardText.Text = text;
            AutomationProperties.SetName(HeardText, text ?? string.Empty);
            HeardConfidence.Text = string.Empty;
        }

        // Engine thread. Hop before touching controls.
        private void OnPhraseHeard(string sourceName, string text, float confidence, bool fired)
            => Dispatcher.BeginInvoke(new Action(() => ShowHeard(sourceName, text, confidence, fired)));

        /// <summary>Shows a recognition: the sentence with the phrase set
        /// apart in SemiBold, the confidence at the line's end (cold when
        /// the phrase fired, quiet when it did not), and the phrase's row
        /// lit when it fired.</summary>
        internal void ShowHeard(string sourceName, string text, float confidence, bool fired)
        {
            string format = fired ? Strings.Instance.Voice_HeardFired_Format : Strings.Instance.Voice_HeardIgnored_Format;
            var (before, after) = FormatRuns.Split(format, sourceName);
            HeardText.Inlines.Clear();
            HeardText.Inlines.Add(new Run(before));
            HeardText.Inlines.Add(new Run(text) { FontWeight = FontWeights.SemiBold });
            HeardText.Inlines.Add(new Run(after));
            AutomationProperties.SetName(HeardText, before + text + after);
            // The line's Text does not follow its runs, so the live region
            // is told of the new sentence here.
            LiveText.Announce(HeardText);
            HeardConfidence.Text = confidence.ToString("F2", CultureInfo.CurrentCulture);
            HeardConfidence.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                fired ? "ColdBrush" : "TextFillColorTertiaryBrush");

            // Only a FIRING recognition lights the row: the engine maps
            // every utterance to its nearest phrase (a rejected "meow"
            // arrives as nearest-"hello"), so lighting on any event made
            // the row claim matches the floor had already refused. The
            // readout line above still shows every attempt with its
            // confidence. The row means "this would have triggered."
            if (!fired) return;
            string norm = VoicePhraseRegistry.NormalizePhrase(text);
            bool lit = false;
            foreach (var row in _rows)
            {
                if (!string.Equals(row.Phrase, norm, StringComparison.Ordinal)) continue;
                lit = true;
                var target = row;
                target.IsActive = true;
                // One timer PER ROW, restarted on each hit: a fresh
                // timer per recognition let the first one clear the
                // flag mid-way through a repeat's flash.
                if (target.FlashTimer == null)
                {
                    target.FlashTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(1400) };
                    target.FlashTimer.Tick += (_, __) => { target.IsActive = false; target.FlashTimer.Stop(); };
                }
                target.FlashTimer.Stop();
                target.FlashTimer.Start();
            }
            // The row-light path testifies: a heard phrase that lights no
            // row names the mismatch instead of leaving it to argument.
            PadForge.Engine.SdlDiagLog.WriteLine("VOICE dialog row "
                + (lit ? "LIT" : "NO MATCH") + " for \"" + norm + "\" (rows=" + _rows.Count + ")");
        }

        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            VoiceMacroService.Enabled = EnabledBox.IsChecked == true;
            VoiceMacroService.ListeningMode = ModeBox.SelectedIndex == 1 ? 1 : 0;
            VoiceMacroService.MinConfidence = (float)ConfidenceSlider.Value;
            ConfidenceText.Text = VoiceMacroService.MinConfidence.ToString("F2", CultureInfo.CurrentCulture);
            // Turning listening on or off changes what the line waits for.
            // Any other setting leaves the last recognition on it.
            bool listening = EnabledBox.IsChecked == true;
            if (listening != _listening)
            {
                _listening = listening;
                ShowWaiting();
            }
            // The registry-changed pipeline persists on phrase edits. A pure
            // settings change rides the dirty queue like any other UI edit.
            try { (Application.Current.MainWindow as MainWindow)?.SettingsService?.MarkDirty(); } catch { }
        }

        private void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            string phrase = PhraseBox.Text;
            if (string.IsNullOrWhiteSpace(phrase)) return;
            string name = VoicePhraseRegistry.Register(phrase, NameBox.Text);
            if (name == null) return;
            ShowLine(string.Format(Strings.Instance.Voice_Registered_Format, name));
            PhraseBox.Text = string.Empty;
            NameBox.Text = string.Empty;
            PhraseBox.Focus();
            RefreshList();
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string phrase && !string.IsNullOrEmpty(phrase))
            {
                VoicePhraseRegistry.Remove(phrase);
                RefreshList();
                // The row and its button are gone, so the focus they held
                // goes to the phrase box.
                PhraseBox.Focus();
            }
        }

        private void PhraseBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                RegisterButton_Click(sender, e);
                e.Handled = true;
            }
        }

        private void RefreshList()
        {
            foreach (var row in _rows)
                try { row.FlashTimer?.Stop(); } catch { }
            _rows.Clear();
            foreach (var p in VoicePhraseRegistry.Phrases)
                _rows.Add(new PhraseRow { Phrase = p.Phrase, Name = p.Name });
            PhraseListBox.ItemsSource = _rows;
            PhrasesBlock.Visibility = _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
