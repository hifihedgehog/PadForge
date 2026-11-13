using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using PadForge.Resources.Strings;
using PadForge.Services;

namespace PadForge.Views
{
    /// <summary>
    /// The removal of BthPS3 from the PC, confirmed against a preview: each
    /// item a removal would take, each one it keeps and why, and what holds
    /// it. The preview reads the PC off the UI thread and changes nothing.
    /// Uninstall stays off until a preview finds something to take and
    /// nothing holds the removal. The same window, with Close alone, lists
    /// what a removal could not take (<see cref="ShowReport"/>). It mirrors
    /// <see cref="UninstallHIDMaestroDialog"/>.
    /// </summary>
    public partial class UninstallBthPS3Dialog : DialogWindow
    {
        /// <summary>One item of a report, as the list shows it.</summary>
        public sealed class EntryRow
        {
            public string Kind { get; init; }
            public string Location { get; init; }

            /// <summary>Why the item stays or was not removed. Null for an
            /// item the removal takes.</summary>
            public string Detail { get; init; }

            /// <summary>The row's first line: the kind, its first letter
            /// upper.</summary>
            public string Title { get; init; }

            /// <summary>What becomes of the item, at the row's end.</summary>
            public string Outcome { get; init; }

            /// <summary>Whether the removal takes the item. Any other
            /// outcome stands in the wait color.</summary>
            public bool IsRemoved { get; init; }

            /// <summary>What a screen reader says for the row.</summary>
            public override string ToString()
                => string.Join(", ", new[] { Title, Location, Outcome, Detail }.Where(part => !string.IsNullOrEmpty(part)));
        }

        private readonly Func<Task<BthPS3Removal.Report>> _preview;
        private int _generation;
        private bool _confirmed;

        private UninstallBthPS3Dialog(Func<Task<BthPS3Removal.Report>> preview)
        {
            _preview = preview;
            InitializeComponent();
            Loaded += (_, _) => CancelButton.Focus();
        }

        /// <summary>Shows the preview and returns true only when the user
        /// clicked Uninstall.</summary>
        internal static bool Confirm(Window owner, Func<Task<BthPS3Removal.Report>> preview)
        {
            var dlg = new UninstallBthPS3Dialog(preview);
            Place(dlg, owner);
            dlg.Loaded += (_, _) => dlg.RunPreview();
            dlg.ShowDialog();
            return dlg._confirmed;
        }

        /// <summary>Lists what a removal could not take, under its status
        /// line, with Close alone.</summary>
        internal static void ShowReport(Window owner, BthPS3Removal.Report report, string statusLine)
        {
            var dlg = new UninstallBthPS3Dialog(null);
            Place(dlg, owner);
            dlg.Subtitle = statusLine;
            dlg.BusyPanel.Visibility = Visibility.Collapsed;
            dlg.RefreshButton.Visibility = Visibility.Collapsed;
            dlg.ActionButton.Visibility = Visibility.Collapsed;
            dlg.CancelButton.Content = Strings.Instance.Common_Close;
            dlg.ShowEntries(RowsFor(report));
            dlg.ShowDialog();
        }

        private static void Place(Window dlg, Window owner)
        {
            if (owner != null) dlg.Owner = owner;
            else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        /// <summary>The rows for a report. A preview lists every item, with
        /// the reason beside each one that stays. A removal lists only the
        /// items it could not take. The reasons and the outcomes read as the
        /// HIDMaestro removal's do.</summary>
        internal static IReadOnlyList<EntryRow> RowsFor(BthPS3Removal.Report report)
        {
            var rows = new List<EntryRow>();
            foreach (var item in report.Items)
            {
                if (!report.IsPreview && item.Outcome != BthPS3Removal.ItemOutcome.Failed) continue;
                string format = item.Outcome switch
                {
                    BthPS3Removal.ItemOutcome.Kept => Strings.Instance.HIDMaestroUninstall_Stays_Format,
                    BthPS3Removal.ItemOutcome.Failed => Strings.Instance.HIDMaestroUninstall_NotRemoved_Format,
                    _ => null,
                };
                string kind = BthPS3Removal.KindText(item.Kind);
                rows.Add(new EntryRow
                {
                    Kind = kind,
                    Location = item.Location,
                    Detail = format != null && !string.IsNullOrEmpty(item.Reason) ? string.Format(format, item.Reason) : null,
                    Title = UninstallHIDMaestroDialog.TitleOf(kind, CultureInfo.CurrentCulture),
                    Outcome = item.Outcome switch
                    {
                        BthPS3Removal.ItemOutcome.Kept => Strings.Instance.HIDMaestroUninstall_OutcomeKept,
                        BthPS3Removal.ItemOutcome.Failed => report.IsPreview
                            ? Strings.Instance.HIDMaestroUninstall_OutcomeUnread
                            : Strings.Instance.HIDMaestroUninstall_OutcomeNotRemoved,
                        _ => Strings.Instance.HIDMaestroUninstall_OutcomeRemoved,
                    },
                    IsRemoved = item.Outcome is not (BthPS3Removal.ItemOutcome.Kept or BthPS3Removal.ItemOutcome.Failed),
                });
            }
            return rows;
        }

        /// <summary>True when a removal after this preview has something to
        /// take and nothing holds it.</summary>
        internal static bool CanRemove(BthPS3Removal.Report report) =>
            report != null && report.Status == BthPS3Removal.RemovalStatus.Previewed;

        /// <summary>The line above the list: what holds the removal, why
        /// BthPS3 stays, or that nothing is left. Null for a preview that
        /// can go ahead.</summary>
        internal static (string Text, bool Caution)? NoteFor(BthPS3Removal.Report report)
        {
            if (report.Status == BthPS3Removal.RemovalStatus.Blocked)
                return (BthPS3Removal.BlockerText(report.Blocker), true);
            if (report.Status == BthPS3Removal.RemovalStatus.Nothing)
                return report.Items.Any(i => i.IsStack && i.Outcome == BthPS3Removal.ItemOutcome.Kept)
                    ? (Strings.Instance.BthPS3Uninstall_DsHidMini, false)
                    : (Strings.Instance.BthPS3Uninstall_Nothing, false);
            return null;
        }

        private async void RunPreview()
        {
            int generation = ++_generation;
            ActionButton.IsEnabled = false;
            RefreshButton.IsEnabled = false;
            BusyPanel.Visibility = Visibility.Visible;
            HideNote();
            EntryFrame.Visibility = Visibility.Collapsed;

            BthPS3Removal.Report report = null;
            string failure = null;
            try { report = await _preview(); }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine($"BTHPS3 removal preview failed: {ex.GetType().Name}: {ex.Message}");
                failure = ex.Message;
            }
            // A preview that ends after the window closed, or after a newer
            // one started, has nothing to show.
            if (generation != _generation || !IsLoaded) return;

            BusyPanel.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = true;
            if (report == null)
            {
                ShowNote(string.Format(Strings.Instance.Status_DriverOperationFailed_Format, failure ?? string.Empty), caution: true);
                return;
            }

            BthPS3Removal.Log(report);
            if (NoteFor(report) is { } note) ShowNote(note.Text, note.Caution);
            ShowEntries(RowsFor(report));
            ActionButton.IsEnabled = CanRemove(report);
        }

        /// <summary>A problem (what holds the removal, a preview that failed)
        /// stands in the wait box. Nothing to remove, and why BthPS3 stays,
        /// are plain lines with no box.</summary>
        private void ShowNote(string text, bool caution)
        {
            NoteText.Text = caution ? text : string.Empty;
            NotePanel.Visibility = caution ? Visibility.Visible : Visibility.Collapsed;
            PlainNoteText.Text = caution ? string.Empty : text;
            PlainNoteText.Visibility = caution ? Visibility.Collapsed : Visibility.Visible;
        }

        private void HideNote()
        {
            NotePanel.Visibility = Visibility.Collapsed;
            PlainNoteText.Visibility = Visibility.Collapsed;
        }

        private void ShowEntries(IReadOnlyList<EntryRow> rows)
        {
            EntryList.ItemsSource = rows;
            EntryFrame.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => RunPreview();

        private void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            _confirmed = true;
            DialogResult = true;
        }
    }
}
