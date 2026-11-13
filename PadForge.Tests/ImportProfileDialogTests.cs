using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using PadForge.Common.ConfigImport;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;
using PadForge.Views;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// Import Profile on the dialogs' frame (#574): the line under the
    /// title, the three counts, the bindings that need a look with the
    /// reason for each, and the ones that came across as they were.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class ImportProfileDialogTests
    {
        /// <summary>The board's file: ten clean bindings would be long, so
        /// two, with one skipped for a reason and one partial with none.</summary>
        internal static ForeignImportResult StarCourier(bool withSlots = true)
        {
            var report = new TranslationReport { CleanCount = 2, PartialCount = 1, SkippedCount = 1 };
            report.Entries.Add(new TranslationEntry { Status = TranslationStatus.Clean, SourcePath = "A", Binding = "Space" });
            report.Entries.Add(new TranslationEntry { Status = TranslationStatus.Partial, SourcePath = "Right Stick", Binding = "Mouse" });
            report.Entries.Add(new TranslationEntry { Status = TranslationStatus.Skipped, SourcePath = "Gyro Tilt Low", ReasonKey = "Import_Tr_GyroZone" });
            report.Entries.Add(new TranslationEntry { Status = TranslationStatus.Clean, SourcePath = "LB", Binding = "To Flight" });
            return new ForeignImportResult
            {
                Kind = ForeignConfigKind.Rewasd,
                Profile = new ProfileData { Name = "Star Courier", SlotCreated = withSlots ? new[] { true } : new[] { false } },
                Report = report,
                ReadsController = true,
            };
        }

        /// <summary>A binding that needs a look says how it came across and
        /// why: Skipped or Partial, then the reason when the report gives
        /// one. The ones with something to say come first, in the file's
        /// order, and a clean binding says nothing.</summary>
        [Fact]
        public void AnImportRowSaysWhyItNeedsALook()
        {
            var rows = ImportConfigDialog.BuildRows(StarCourier().Report);
            Assert.Equal(new[] { "Gyro Tilt Low", "Right Stick", "A", "LB" }, rows.Select(r => r.Source));
            Assert.Equal(new[] { TranslationStatus.Skipped, TranslationStatus.Partial, TranslationStatus.Clean, TranslationStatus.Clean },
                rows.Select(r => r.Status));

            string reason = Strings.Instance.Import_Tr_GyroZone;
            Assert.Equal(reason, rows[0].Reason);
            Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Instance.Import_Detail_Format, Strings.Instance.Workshop_StatSkipped, reason),
                rows[0].Detail);
            Assert.Null(rows[1].Reason);
            Assert.Equal(Strings.Instance.Workshop_StatPartial, rows[1].Detail);
            Assert.Null(rows[2].Detail);
            Assert.Null(rows[3].Detail);

            Assert.Null(ImportConfigDialog.DetailText(TranslationStatus.Clean, "any"));
            Assert.Equal(Strings.Instance.Workshop_StatSkipped, ImportConfigDialog.DetailText(TranslationStatus.Skipped, null));
            Assert.Equal(Strings.Instance.Workshop_StatSkipped, ImportConfigDialog.DetailText(TranslationStatus.Skipped, string.Empty));
        }

        /// <summary>The dialog's parts for a file: the line under the title
        /// with the file's name set apart, a wait tile for a count of
        /// bindings that did not come across whole, and the two lists.</summary>
        [Fact]
        public void TheImportPreviewShowsWhatCameAcross()
        {
            DialogTestHost.Run(() =>
            {
                var dialog = new ImportConfigDialog(@"C:\Users\Public\Documents\reWASD\Profiles\Star Courier.rewasd", StarCourier());
                try
                {
                    var s = Strings.Instance;
                    Assert.True(dialog.IsWide);
                    Assert.True(dialog.ShowClose);
                    Assert.Equal(s.Import_DialogTitle, dialog.Title);

                    // The line under the title, as one sentence, with the
                    // file's name a run of its own.
                    var line = (TextBlock)dialog.Subtitle;
                    string whole = string.Format(CultureInfo.CurrentCulture, s.Import_Source_Format, "Star Courier.rewasd", "reWASD")
                        + " " + string.Format(CultureInfo.CurrentCulture, s.Import_ProfileName_Format, ImportConfigDialog.PreviewName(StarCourier()));
                    Assert.Equal(whole, AutomationProperties.GetName(line));
                    Assert.Equal(whole, string.Concat(line.Inlines.OfType<Run>().Select(run => run.Text)));
                    Assert.Contains(line.Inlines.OfType<Run>(), run => run.Text == "Star Courier.rewasd" && run.FontSize == 13);

                    // The counts, and the wait look for the two that did not
                    // come across whole.
                    Assert.Equal("2", ((TextBlock)dialog.FindName("StatCleanNum")).Text);
                    Assert.Equal("1", ((TextBlock)dialog.FindName("StatPartialNum")).Text);
                    Assert.Equal("1", ((TextBlock)dialog.FindName("StatSkippedNum")).Text);
                    var wait = (Style)dialog.FindResource("DialogStatTileWait");
                    var plain = (Style)dialog.FindResource("DialogStatTile");
                    Assert.Same(plain, ((Border)dialog.FindName("StatCleanTile")).Style);
                    Assert.Same(wait, ((Border)dialog.FindName("StatPartialTile")).Style);
                    Assert.Same(wait, ((Border)dialog.FindName("StatSkippedTile")).Style);

                    var look = ((ItemsControl)dialog.FindName("NeedsLookList")).Items.Cast<ImportReportRow>().ToList();
                    var clean = ((ItemsControl)dialog.FindName("CleanList")).Items.Cast<ImportReportRow>().ToList();
                    Assert.Equal(new[] { "Gyro Tilt Low", "Right Stick" }, look.Select(r => r.Source));
                    Assert.Equal(new[] { "A", "LB" }, clean.Select(r => r.Source));
                    Assert.Equal(Visibility.Visible, ((UIElement)dialog.FindName("NeedsLookBlock")).Visibility);
                    Assert.Equal(Visibility.Visible, ((UIElement)dialog.FindName("CleanBlock")).Visibility);
                    Assert.Equal(s.Import_NoteController, ((TextBlock)dialog.FindName("NoteText")).Text);
                    Assert.True(((Button)dialog.FindName("ImportButton")).IsEnabled);
                    Assert.Null(dialog.ImportedProfileName);
                }
                finally { dialog.Close(); }

                // A file that maps nothing: Import is off, and the note says
                // why. A report with nothing in a list leaves that list out.
                var nothing = StarCourier(withSlots: false);
                nothing.Report.Entries.Clear();
                nothing.Report.CleanCount = nothing.Report.PartialCount = nothing.Report.SkippedCount = 0;
                var empty = new ImportConfigDialog(@"C:\empty.rewasd", nothing);
                try
                {
                    Assert.False(((Button)empty.FindName("ImportButton")).IsEnabled);
                    Assert.Equal(Strings.Instance.Import_NothingToImport, ((TextBlock)empty.FindName("NoteText")).Text);
                    Assert.Equal(Visibility.Collapsed, ((UIElement)empty.FindName("NeedsLookBlock")).Visibility);
                    Assert.Equal(Visibility.Collapsed, ((UIElement)empty.FindName("CleanBlock")).Visibility);
                    Assert.Same(empty.FindResource("DialogStatTile"), ((Border)empty.FindName("StatSkippedTile")).Style);
                }
                finally { empty.Close(); }
            });
        }
    }
}
