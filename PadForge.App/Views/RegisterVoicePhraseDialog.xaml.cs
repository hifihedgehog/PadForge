using System;
using System.Windows;
using System.Windows.Input;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using PadForge.Services;

namespace PadForge.Views
{
    /// <summary>
    /// Voice macro management (issue #317): the settings band (enable,
    /// listening mode, confidence floor), a live "heard" readout naming the
    /// microphone that heard it, and the type-and-name phrase registration
    /// flow with a Remove list. Modeled on <see cref="RegisterNfcTagDialog"/>.
    /// There is no source picker: phrases live on the devices that carry the
    /// microphones, and every reachable microphone runs its own session.
    /// </summary>
    public partial class RegisterVoicePhraseDialog : Wpf.Ui.Controls.FluentWindow
    {
        /// <summary>Row item for the phrase list: the registry entry plus a
        /// short-lived highlight so a recognized phrase lights its row with
        /// no mapping required.</summary>
        public sealed class PhraseRow : System.ComponentModel.INotifyPropertyChanged
        {
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
        }

        private readonly System.Collections.ObjectModel.ObservableCollection<PhraseRow> _rows = new();

        private Action<string, string, float, bool> _heardHandler;
        private VoiceMacroService _subscribedSvc;
        private bool _loading = true;

        public RegisterVoicePhraseDialog()
        {
            InitializeComponent();
            MouseLeftButtonDown += (_, __) => { try { DragMove(); } catch { } };

            EnabledBox.IsChecked = VoiceMacroService.Enabled;
            ModeBox.SelectedIndex = VoiceMacroService.ListeningMode == 1 ? 1 : 0;
            ConfidenceSlider.Value = Math.Clamp(VoiceMacroService.MinConfidence, 0.5, 0.99);
            ConfidenceText.Text = VoiceMacroService.MinConfidence.ToString("F2");
            _loading = false;

            RefreshList();

            var svc = VoiceMacroService.Active;
            if (svc != null)
            {
                _heardHandler = OnPhraseHeard;
                svc.PhraseHeard += _heardHandler;
                _subscribedSvc = svc;
            }

            Closed += (s, e) => Unsubscribe();
        }

        private void Unsubscribe()
        {
            if (_subscribedSvc != null && _heardHandler != null)
            {
                try { _subscribedSvc.PhraseHeard -= _heardHandler; } catch { }
            }
            _subscribedSvc = null;
            _heardHandler = null;
        }

        private void OnPhraseHeard(string sourceName, string text, float confidence, bool fired)
        {
            // Engine thread; hop before touching controls.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                HeardText.Text = "[" + sourceName + "] " + string.Format(
                    fired ? Strings.Instance.Voice_HeardFired_Format : Strings.Instance.Voice_HeardIgnored_Format,
                    text, confidence);
                // Light the matching row, mapping or not. Any accepted
                // recognition qualifies; the readout line above carries the
                // confidence and whether it fired.
                // Only a FIRING recognition lights the row: the engine maps
                // every utterance to its nearest phrase (a rejected "meow"
                // arrives as nearest-"hello"), so lighting on any event made
                // the row claim matches the floor had already refused. The
                // readout line above still shows every attempt with its
                // confidence; the row means "this would have triggered."
                if (!fired) return;
                string norm = VoicePhraseRegistry.NormalizePhrase(text);
                bool lit = false;
                foreach (var row in _rows)
                {
                    if (!string.Equals(row.Phrase, norm, StringComparison.Ordinal)) continue;
                    lit = true;
                    var target = row;
                    target.IsActive = true;
                    var t = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(1400) };
                    t.Tick += (_, __) => { target.IsActive = false; t.Stop(); };
                    t.Start();
                }
                // The row-light path testifies: a heard phrase that lights no
                // row names the mismatch instead of leaving it to argument.
                PadForge.Engine.SdlDiagLog.WriteLine("VOICE dialog row "
                    + (lit ? "LIT" : "NO MATCH") + " for \"" + norm + "\" (rows=" + _rows.Count + ")");
            }));
        }

        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            VoiceMacroService.Enabled = EnabledBox.IsChecked == true;
            VoiceMacroService.ListeningMode = ModeBox.SelectedIndex == 1 ? 1 : 0;
            VoiceMacroService.MinConfidence = (float)ConfidenceSlider.Value;
            ConfidenceText.Text = VoiceMacroService.MinConfidence.ToString("F2");
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
            HeardText.Text = string.Format(Strings.Instance.Voice_Registered_Format, name);
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

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Unsubscribe();
            Close();
        }

        private void RefreshList()
        {
            _rows.Clear();
            foreach (var p in VoicePhraseRegistry.Phrases)
                _rows.Add(new PhraseRow { Phrase = p.Phrase, Name = p.Name });
            PhraseListBox.ItemsSource = _rows;
        }
    }
}
