using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Engine.Common;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    /// <summary>
    /// Press-to-learn flow for handheld hidden buttons (issue #343), the
    /// NFC registration dialog's shape. Start runs three timed phases on
    /// the shared learn session (hands off, press and hold, release);
    /// whatever the press changed, a key chord through the hooks or a
    /// field in a vendor report, becomes a candidate the user names and
    /// registers. The list below shows the learned set with Remove, and
    /// the footer exports or imports it as a file so one owner's work
    /// seeds every machine of that model.
    /// </summary>
    public partial class LearnHandheldButtonDialog : Wpf.Ui.Controls.FluentWindow
    {
        private sealed class CandidateRow
        {
            public HandheldLearnSession.Candidate Candidate;
            public string Description => Candidate.Describe();
        }

        private sealed class EntryRow
        {
            public string Name { get; set; }
            public int Button { get; set; }
            public string Description { get; set; }
        }

        private sealed class ExportFile
        {
            [JsonPropertyName("padforgeHandheldButtons")] public int Version { get; set; } = 1;
            [JsonPropertyName("machine")] public string Machine { get; set; }
            [JsonPropertyName("buttons")] public List<PadForge.Services.HandheldButtonData> Buttons { get; set; }
        }

        private readonly HandheldButtonsDevice _dev;
        private readonly DispatcherTimer _phaseTimer = new();
        private HandheldLearnSession _session;
        private int[] _chord;
        private List<HandheldLearnSession.Candidate> _candidates = new();

        public LearnHandheldButtonDialog()
        {
            InitializeComponent();
            MouseLeftButtonDown += (_, __) => { try { DragMove(); } catch { } };

            MachineText.Text = MachineIdentity.Current.DisplayName;
            RefreshDaemon();
            _dev = HandheldButtonsDevice.Active;
            if (_dev == null)
            {
                StatusText.Text = Strings.Instance.Handheld_NoDevice;
                StartBtn.IsEnabled = false;
            }
            else
            {
                // Arm the capture: the hooks install and every vendor
                // collection opens, so the first Start already sees the press.
                HandheldButtonRegistry.LearnCaptureActive = true;
                _dev.SyncReadersNow();
            }
            RefreshList();
            _phaseTimer.Tick += PhaseTimer_Tick;
            Closed += (s, e) => Teardown();
        }

        private void RefreshDaemon()
        {
            string running = HandheldDaemonWatch.Running;
            if (string.IsNullOrEmpty(running))
            {
                DaemonText.Visibility = Visibility.Collapsed;
                return;
            }
            DaemonText.Text = string.Format(Strings.Instance.Handheld_DaemonWarning_Format, running);
            DaemonText.Visibility = Visibility.Visible;
        }

        private void Teardown()
        {
            _phaseTimer.Stop();
            if (_session != null)
            {
                _session.SetPhase(HandheldLearnSession.Phase.Done);
                _session = null;
            }
            _dev?.EndLearn();
            HandheldButtonRegistry.LearnCaptureActive = false;
        }

        // ── Learn ──

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            // The feature can be switched off while this dialog is open,
            // which retires the row; say so instead of running a pass that
            // can only end in "nothing found".
            if (_dev == null || !_dev.IsAttached)
            {
                StatusText.Text = Strings.Instance.Handheld_NoDevice;
                StartBtn.IsEnabled = false;
                return;
            }
            _chord = null;
            _candidates = new List<HandheldLearnSession.Candidate>();
            CandidateBox.Visibility = Visibility.Collapsed;
            CandidateBox.ItemsSource = null;
            ChordText.Text = Strings.Instance.Nfc_Waiting;
            RegisterBtn.IsEnabled = false;
            StartBtn.IsEnabled = false;

            _session = new HandheldLearnSession();
            _session.SetPhase(HandheldLearnSession.Phase.Idle);
            _dev.BeginLearn(_session);
            StatusText.Text = Strings.Instance.Handheld_PhaseIdle;
            _phaseTimer.Interval = TimeSpan.FromMilliseconds(HandheldLearnSession.IdleMs);
            _phaseTimer.Start();
        }

        private void PhaseTimer_Tick(object sender, EventArgs e)
        {
            var session = _session;
            if (session == null) { _phaseTimer.Stop(); return; }
            switch (session.Current)
            {
                case HandheldLearnSession.Phase.Idle:
                    session.SetPhase(HandheldLearnSession.Phase.Press);
                    StatusText.Text = Strings.Instance.Handheld_PhasePress;
                    _phaseTimer.Interval = TimeSpan.FromMilliseconds(HandheldLearnSession.PressMs);
                    break;
                case HandheldLearnSession.Phase.Press:
                    session.SetPhase(HandheldLearnSession.Phase.Release);
                    StatusText.Text = Strings.Instance.Handheld_PhaseRelease;
                    _phaseTimer.Interval = TimeSpan.FromMilliseconds(HandheldLearnSession.ReleaseMs);
                    break;
                default:
                    _phaseTimer.Stop();
                    FinishLearn(session);
                    break;
            }
        }

        private void FinishLearn(HandheldLearnSession session)
        {
            _candidates = session.Finish();
            _chord = session.ChordKeys;
            _dev?.EndLearn();
            _session = null;
            StartBtn.IsEnabled = true;

            bool any = (_chord != null && _chord.Length > 0) || _candidates.Count > 0;
            if (!any)
            {
                StatusText.Text = Strings.Instance.Handheld_NothingFound;
                return;
            }

            var parts = new List<string>();
            if (_chord != null && _chord.Length > 0)
                parts.Add(string.Format(Strings.Instance.Handheld_ChordFormat, HandheldKeyNames.Describe(_chord)));
            if (_candidates.Count == 1)
                parts.Add(_candidates[0].Describe());
            ChordText.Text = parts.Count > 0 ? string.Join("  |  ", parts) : string.Empty;

            if (_candidates.Count > 1)
            {
                CandidateBox.ItemsSource = _candidates.Select(c => new CandidateRow { Candidate = c }).ToList();
                CandidateBox.SelectedIndex = 0;
                CandidateBox.Visibility = Visibility.Visible;
            }

            StatusText.Text = Strings.Instance.Handheld_Captured;
            RegisterBtn.IsEnabled = true;
            if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Focus();
        }

        private void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            var entry = new HandheldButtonRegistry.Entry { Name = NameBox.Text };
            if (_chord != null && _chord.Length > 0) entry.Keys = (int[])_chord.Clone();
            HandheldLearnSession.Candidate chosen = null;
            if (_candidates.Count == 1) chosen = _candidates[0];
            else if (_candidates.Count > 1) chosen = (CandidateBox.SelectedItem as CandidateRow)?.Candidate ?? _candidates[0];
            if (chosen != null)
            {
                entry.Collection = chosen.Collection;
                entry.ReportId = chosen.ReportId;
                entry.ByteIndex = chosen.ByteIndex;
                entry.Mask = chosen.Mask;
                entry.Value = chosen.Value;
                entry.ValueKind = chosen.Kind;
            }
            if (!entry.HasChord && !entry.HasReport) return;

            HandheldButtonRegistry.StampMachine(MachineIdentity.Current.Key);
            var stored = HandheldButtonRegistry.Register(entry);
            if (stored == null) return;
            StatusText.Text = string.Format(Strings.Instance.Handheld_RegisteredFormat, stored.Name);
            NameBox.Text = string.Empty;
            ChordText.Text = Strings.Instance.Nfc_Waiting;
            CandidateBox.Visibility = Visibility.Collapsed;
            CandidateBox.ItemsSource = null;
            _chord = null;
            _candidates = new List<HandheldLearnSession.Candidate>();
            RegisterBtn.IsEnabled = false;
            RefreshList();
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is int button)
            {
                HandheldButtonRegistry.Remove(button);
                RefreshList();
            }
        }

        private void NameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && RegisterBtn.IsEnabled)
            {
                RegisterButton_Click(sender, e);
                e.Handled = true;
            }
        }

        // ── Export / import ──

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = Strings.Instance.Handheld_Export,
                Filter = "PadForge Handheld Buttons (*.json)|*.json",
                FileName = SafeFileName(MachineIdentity.Current.DisplayName) + "-hidden-buttons.json",
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var file = new ExportFile
                {
                    Machine = HandheldButtonRegistry.MachineKey,
                    Buttons = HandheldButtonRegistry.SaveRegistry().Select(PadForge.Services.HandheldButtonData.From).ToList(),
                };
                File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
                StatusText.Text = string.Format(Strings.Instance.Handheld_ExportedFormat, file.Buttons.Count);
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Strings.Instance.Handheld_Import,
                Filter = "PadForge Handheld Buttons (*.json)|*.json",
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var file = JsonSerializer.Deserialize<ExportFile>(File.ReadAllText(dlg.FileName));
                int added = 0;
                if (file?.Buttons != null)
                {
                    HandheldButtonRegistry.StampMachine(file.Machine ?? MachineIdentity.Current.Key);
                    foreach (var b in file.Buttons)
                    {
                        var entry = b?.ToEntry();
                        if (entry == null) continue;
                        if (HandheldButtonRegistry.Register(entry) != null) added++;
                    }
                }
                StatusText.Text = string.Format(Strings.Instance.Handheld_ImportedFormat, added);
                RefreshList();
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private static string SafeFileName(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = (s ?? "machine").Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray();
            string r = new string(chars).Trim('-');
            return r.Length == 0 ? "machine" : r;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Teardown();
            Close();
        }

        private void RefreshList()
        {
            var rows = HandheldButtonRegistry.Entries.Select(x => new EntryRow
            {
                Name = x.Name,
                Button = x.Button,
                Description = HandheldKeyNames.DescribeEntry(x),
            }).ToList();
            ButtonListBox.ItemsSource = rows;
            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
