using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
        public Brush DotBrush { get; init; }
        public Visibility ReasonVisibility => string.IsNullOrEmpty(Reason) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Shows what another tool's config comes to before it joins the profile
    /// list (#561). What came across with a difference, or did not, sits first
    /// with its reason in full. Accepting hands the profile to the sink the
    /// Workshop and starter profiles land through, so nothing about it stays
    /// special after that. Nothing the file names has run or will run.
    /// </summary>
    public partial class ImportConfigDialog
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

            // ExtendsContentIntoTitleBar leaves no non-client area, so without
            // this the dialog cannot be moved. Same wiring as its siblings.
            MouseLeftButtonDown += (_, __) => { try { DragMove(); } catch { } };

            SourceText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Instance.Import_Source_Format,
                Path.GetFileName(path), result.ToolName);
            ProfileNameText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Instance.Import_ProfileName_Format,
                result.Profile?.Name ?? string.Empty);

            StatCleanNum.Text = result.Report.CleanCount.ToString(CultureInfo.CurrentCulture);
            StatPartialNum.Text = result.Report.PartialCount.ToString(CultureInfo.CurrentCulture);
            StatSkippedNum.Text = result.Report.SkippedCount.ToString(CultureInfo.CurrentCulture);

            var notes = new List<string>();
            if (!result.HasSlots) notes.Add(Strings.Instance.Import_NothingToImport);
            else
            {
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

            ImportButton.IsEnabled = result.HasSlots;
            ReportList.ItemsSource = BuildRows(result.Report);
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
                .Select(e => new ImportReportRow
                {
                    Source = e.SourcePath,
                    Target = e.Binding,
                    Reason = ReasonText(e),
                    DotBrush = e.Status switch
                    {
                        TranslationStatus.Clean => WorkshopManifestRowItem.CleanBrush,
                        TranslationStatus.Partial => WorkshopManifestRowItem.PartialBrush,
                        _ => WorkshopManifestRowItem.SkippedBrush,
                    },
                })
                .ToList();
        }

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

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void HeaderClose_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
