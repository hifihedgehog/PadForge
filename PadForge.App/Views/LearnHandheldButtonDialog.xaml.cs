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
    /// NFC registration dialog's shape. A learn runs three timed phases on
    /// the shared learn session (hands off, press and hold, let go). It
    /// starts as the dialog opens, and Start Over runs it again.
    /// Whatever the press changed, a key chord through the hooks or a
    /// field in a vendor report, becomes a candidate the user names and
    /// registers. The list below shows the learned set with Remove, and
    /// the footer exports or imports it as a file so one owner's work
    /// seeds every machine of that model.
    /// </summary>
    public partial class LearnHandheldButtonDialog : DialogWindow
    {
        /// <summary>The steps of a learn, as the live line counts them.</summary>
        private const int Steps = 3;

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

            public override string ToString() => Name;
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

            ListHead.Text = string.Format(Strings.Instance.Handheld_LearnedOn_Format, MachineIdentity.Current.DisplayName);
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
                // collection opens, so the first learn already sees the press.
                HandheldButtonRegistry.LearnCaptureActive = true;
                _dev.SyncReadersNow();
                // The learn runs as the dialog opens: the line under the
                // title says to press the button when asked.
                RoutedEventHandler opened = null;
                opened = (_, _) =>
                {
                    Loaded -= opened;
                    StartLearn();
                };
                Loaded += opened;
            }
            RefreshList();
            _phaseTimer.Tick += PhaseTimer_Tick;
            Loaded += (_, _) => NameBox.Focus();
            Closed += (s, e) => Teardown();
        }

        private void RefreshDaemon()
        {
            string running = HandheldDaemonWatch.Running;
            if (string.IsNullOrEmpty(running))
            {
                DaemonBox.Visibility = Visibility.Collapsed;
                return;
            }
            DaemonText.Text = string.Format(Strings.Instance.Handheld_DaemonWarning_Format, running);
            DaemonBox.Visibility = Visibility.Visible;
        }

        /// <summary>The dot of the live line: ember while a learn runs, the
        /// wait color while none does.</summary>
        private void ShowRunning(bool running)
            => LiveDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, running ? "EmberBrush" : "WaitBrush");

        /// <summary>The end of the live line during a learn: which step
        /// this is, in the body face.</summary>
        private void ShowStep(int step)
        {
            ChordText.ClearValue(System.Windows.Controls.TextBlock.FontFamilyProperty);
            ChordText.Text = string.Format(Strings.Instance.Handheld_Step_Format, step, Steps);
            ChordText.ToolTip = null;
        }

        /// <summary>The end of the live line after a learn: what the press
        /// changed, in the code face, or nothing. A long source is cut
        /// short on the line and whole in its tooltip.</summary>
        private void ShowSource(string source)
        {
            ChordText.SetResourceReference(System.Windows.Controls.TextBlock.FontFamilyProperty, "TelemetryFontFamily");
            ChordText.Text = source ?? string.Empty;
            ChordText.ToolTip = string.IsNullOrEmpty(source) ? null : source;
        }

        private void Teardown()
        {
            _phaseTimer.Stop();
            ShowRunning(false);
            if (_session != null)
            {
                _session.SetPhase(HandheldLearnSession.Phase.Done);
                _session = null;
            }
            _dev?.EndLearn();
            HandheldButtonRegistry.LearnCaptureActive = false;
        }

        // ── Learn ──

        private void StartButton_Click(object sender, RoutedEventArgs e) => StartLearn();

        /// <summary>Runs a learn from its first step. The dialog's opening
        /// starts one, and Start Over another.</summary>
        private void StartLearn()
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
            SourceRow.Visibility = Visibility.Collapsed;
            CandidateBox.ItemsSource = null;
            RegisterBtn.IsEnabled = false;
            StartBtn.IsEnabled = false;

            _session = new HandheldLearnSession();
            _session.SetPhase(HandheldLearnSession.Phase.Idle);
            _dev.BeginLearn(_session);
            StatusText.Text = Strings.Instance.Handheld_PhaseIdle;
            ShowStep(1);
            ShowRunning(true);
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
                    ShowStep(2);
                    _phaseTimer.Interval = TimeSpan.FromMilliseconds(HandheldLearnSession.PressMs);
                    break;
                case HandheldLearnSession.Phase.Press:
                    session.SetPhase(HandheldLearnSession.Phase.Release);
                    StatusText.Text = Strings.Instance.Handheld_PhaseRelease;
                    ShowStep(3);
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
            ShowRunning(false);

            var c = session.Counts;
            int collections = _dev?.OpenCollections.Length ?? 0;
            int classes = WmiEventRuntime.Subscribed.Length;
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"Handheld learn: watched {collections} collections, {classes} WMI classes; reports idle/press/release {c.IdleReports}/{c.PressReports}/{c.ReleaseReports}, events {c.IdleEvents}/{c.PressEvents}/{c.ReleaseEvents}, chord {(_chord == null ? "none" : HandheldKeyNames.Describe(_chord))}, candidates {_candidates.Count}");

            bool any = (_chord != null && _chord.Length > 0) || _candidates.Count > 0;
            if (!any)
            {
                ShowSource(null);
                StatusText.Text = Strings.Instance.Handheld_NothingFound + " "
                    + string.Format(Strings.Instance.Handheld_NothingFoundDetail_Format, collections, classes, c.PressReports, c.PressEvents);
                return;
            }

            var parts = new List<string>();
            if (_chord != null && _chord.Length > 0)
                parts.Add(string.Format(Strings.Instance.Handheld_ChordFormat, HandheldKeyNames.Describe(_chord)));
            if (_candidates.Count == 1)
                parts.Add(_candidates[0].Describe());
            ShowSource(parts.Count > 0 ? string.Join("  |  ", parts) : null);

            if (_candidates.Count > 1)
            {
                CandidateBox.ItemsSource = _candidates.Select(c => new CandidateRow { Candidate = c }).ToList();
                CandidateBox.SelectedIndex = 0;
                SourceRow.Visibility = Visibility.Visible;
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
            if (chosen != null && chosen.IsWmi)
            {
                entry.WmiClass = chosen.Collection;
                entry.WmiProperty = chosen.WmiProperty;
                entry.WmiValue = chosen.WmiValue ?? string.Empty;
            }
            else if (chosen != null)
            {
                entry.Collection = chosen.Collection;
                entry.ReportId = chosen.ReportId;
                entry.ByteIndex = chosen.ByteIndex;
                entry.Mask = chosen.Mask;
                entry.Value = chosen.Value;
                entry.ValueKind = chosen.Kind;
            }
            if (!entry.HasAnyPath) return;

            HandheldButtonRegistry.StampMachine(MachineIdentity.Current.Key);
            var stored = HandheldButtonRegistry.Register(entry);
            if (stored == null) return;
            StatusText.Text = string.Format(Strings.Instance.Handheld_RegisteredFormat, stored.Name);
            NameBox.Text = string.Empty;
            ShowSource(null);
            SourceRow.Visibility = Visibility.Collapsed;
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
                // The row and its button are gone, so the focus they held
                // goes to the name.
                NameBox.Focus();
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
                Filter = Strings.Instance.FileDialog_HandheldButtonsFilter,
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
                Filter = Strings.Instance.FileDialog_HandheldButtonsFilter,
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
            ListFrame.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
