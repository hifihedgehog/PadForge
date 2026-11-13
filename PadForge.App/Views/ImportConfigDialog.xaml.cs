using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using PadForge.Common;
using PadForge.Common.ConfigImport;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;

namespace PadForge.Views
{
    /// <summary>One line of the import preview.</summary>
    public sealed class ImportReportRow
    {
        public string Source { get; init; }
        public string Target { get; init; }
        public string Arrow => string.IsNullOrEmpty(Target) ? string.Empty : "→";
        public string Reason { get; init; }

        /// <summary>How the binding came across.</summary>
        public TranslationStatus Status { get; init; }

        /// <summary>The line under a binding that needs a look: Skipped or
        /// Partial, then the reason when there is one. Null for a binding
        /// that came across as it was.</summary>
        public string Detail { get; init; }
    }

    /// <summary>
    /// Shows what another tool's config comes to before it joins the profile
    /// list (#561). What came across with a difference, or did not, sits first
    /// with its reason in full. Accepting hands the profile to the sink the
    /// Workshop and starter profiles land through, so nothing about it stays
    /// special after that. Nothing the file names runs before the user
    /// accepts, and the notes name every program its buttons or its load
    /// will start.
    /// </summary>
    public partial class ImportConfigDialog : DialogWindow
    {
        private readonly ForeignImportResult _result;

        /// <summary>Set by the caller. Takes the built profile and returns the
        /// deduped name it was registered under.</summary>
        public Func<ProfileData, bool, string> ImportSink { get; set; }

        /// <summary>The name it landed under, or null when dismissed.</summary>
        public string ImportedProfileName { get; private set; }

        public ImportConfigDialog(string path, ForeignImportResult result)
        {
            InitializeComponent();
            _result = result ?? throw new ArgumentNullException(nameof(result));

            // The line under the title: the file, set apart in the code
            // face, the tool it is from, and the profile it becomes.
            string file = Path.GetFileName(path);
            var (before, after) = FormatRuns.Split(Strings.Instance.Import_Source_Format, result.ToolName);
            string becomes = string.Format(CultureInfo.CurrentCulture, Strings.Instance.Import_ProfileName_Format,
                PreviewName(result));
            var name = new Run(file) { FontSize = 13 };
            name.SetResourceReference(TextElement.FontFamilyProperty, "TelemetryFontFamily");
            SourceText.Inlines.Add(new Run(before));
            SourceText.Inlines.Add(name);
            SourceText.Inlines.Add(new Run(after + " " + becomes));
            AutomationProperties.SetName(SourceText, before + file + after + " " + becomes);

            SetTile(StatCleanTile, StatCleanNum, StatCleanLabel, result.Report.CleanCount, problem: false);
            SetTile(StatPartialTile, StatPartialNum, StatPartialLabel, result.Report.PartialCount, problem: true);
            SetTile(StatSkippedTile, StatSkippedNum, StatSkippedLabel, result.Report.SkippedCount, problem: true);

            var notes = new List<string>();
            if (!result.HasSlots) notes.Add(Strings.Instance.Import_NothingToImport);
            else
            {
                if (result.Starts.Count > 0)
                    notes.Add(string.Format(CultureInfo.CurrentCulture, Strings.Instance.Import_NoteStarts_Format, string.Join(", ", result.Starts)));
                if (result.Runs.Count > 0)
                    notes.Add(string.Format(CultureInfo.CurrentCulture, Strings.Instance.Import_NoteRuns_Format, string.Join(", ", result.Runs)));
                if (result.ReadsController) notes.Add(Strings.Instance.Import_NoteController);
                if (result.ReadsKeyboard && result.ReadsMouse)
                    notes.Add(string.Format(CultureInfo.CurrentCulture, Strings.Instance.Import_NoteAssignsTwo_Format,
                        Strings.Instance.Devices_AllKeyboardsMerged, Strings.Instance.Devices_AllMiceMerged));
                else if (result.ReadsKeyboard || result.ReadsMouse)
                    notes.Add(string.Format(CultureInfo.CurrentCulture, Strings.Instance.Import_NoteAssignsOne_Format,
                        result.ReadsKeyboard ? Strings.Instance.Devices_AllKeyboardsMerged : Strings.Instance.Devices_AllMiceMerged));
            }
            NoteText.Text = string.Join(" ", notes);
            NoteText.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            // With no note the body ends on the last list, which keeps room
            // over the foot.
            BodyPanel.Margin = new Thickness(0, 0, 0, notes.Count > 0 ? 0 : 12);

            ImportButton.IsEnabled = result.HasSlots;

            var rows = BuildRows(result.Report);
            var look = rows.Where(r => r.Status != TranslationStatus.Clean).ToList();
            var clean = rows.Where(r => r.Status == TranslationStatus.Clean).ToList();
            NeedsLookList.ItemsSource = look;
            CleanList.ItemsSource = clean;
            NeedsLookBlock.Visibility = look.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            CleanBlock.Visibility = clean.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Import takes the focus when there is something to import, and
            // Cancel when there is not.
            Loaded += (_, _) => { if (ImportButton.IsEnabled) ImportButton.Focus(); else CancelButton.Focus(); };
        }

        /// <summary>A count's tile, as the board draws the three: a zero in
        /// the quiet color, and a count above zero of bindings that did not
        /// come across whole in the wait look.</summary>
        private static void SetTile(Border tile, TextBlock number, TextBlock label, int count, bool problem)
        {
            number.Text = count.ToString(CultureInfo.CurrentCulture);
            bool wait = problem && count > 0;
            tile.SetResourceReference(StyleProperty, wait ? "DialogStatTileWait" : "DialogStatTile");
            number.SetResourceReference(TextBlock.ForegroundProperty,
                wait ? "WaitBrush" : count == 0 ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush");
            if (wait) label.SetResourceReference(TextBlock.ForegroundProperty, "WaitMutedBrush");
        }

        /// <summary>The report's lines, the ones with something to say first,
        /// keeping the file's order inside each group.</summary>
        internal static List<ImportReportRow> BuildRows(TranslationReport report)
        {
            return report.Entries
                .OrderBy(e => e.Status switch
                {
                    TranslationStatus.Skipped => 0,
                    TranslationStatus.Partial => 1,
                    _ => 2,
                })
                .Select(e =>
                {
                    string reason = ReasonText(e);
                    return new ImportReportRow
                    {
                        Source = e.SourcePath,
                        Target = e.Binding,
                        Reason = reason,
                        Status = e.Status,
                        Detail = DetailText(e.Status, reason),
                    };
                })
                .ToList();
        }

        /// <summary>What a binding that needs a look says under its name:
        /// Skipped or Partial, and after it the reason when the report
        /// gives one. Null for a clean binding.</summary>
        internal static string DetailText(TranslationStatus status, string reason)
        {
            if (status == TranslationStatus.Clean) return null;
            string word = status == TranslationStatus.Skipped
                ? Strings.Instance.Workshop_StatSkipped
                : Strings.Instance.Workshop_StatPartial;
            return string.IsNullOrEmpty(reason)
                ? word
                : string.Format(CultureInfo.CurrentCulture, Strings.Instance.Import_Detail_Format, word, reason);
        }

        /// <summary>The name the profile will land under: the sink renames a
        /// name that is taken, and the preview says the same name.</summary>
        internal static string PreviewName(ForeignImportResult result)
            => PadForge.Common.Input.SettingsManager.UniqueProfileName(result.Profile?.Name);

        internal static string ReasonText(TranslationEntry entry)
        {
            if (string.IsNullOrEmpty(entry.ReasonKey)) return null;
            string raw = Strings.Get(entry.ReasonKey);
            if (entry.ReasonArgs == null || entry.ReasonArgs.Count == 0) return raw;
            try
            {
                return string.Format(CultureInfo.CurrentCulture, raw, entry.ReasonArgs.Cast<object>().ToArray());
            }
            catch (FormatException)
            {
                return raw;
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            if (ImportSink == null || !_result.HasSlots)
            {
                DialogResult = false;
                return;
            }
            ImportedProfileName = ImportSink(_result.Profile, false);
            DialogResult = true;
        }
    }
}
