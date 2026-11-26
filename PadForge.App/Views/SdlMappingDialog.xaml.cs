using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.Services.Catalog;

namespace PadForge.Views
{
    /// <summary>
    /// Paste SDL Mapping, Find a Mapping, Guess a Layout and Add Rows From a
    /// File or the Clipboard (#600 items 47 to 49): one dialog, so a row
    /// pasted, picked, guessed or added passes the same check
    /// (<see cref="SdlMappingRow.TryParse"/>) and the same choice. The caller
    /// applies what it returns.
    /// </summary>
    public partial class SdlMappingDialog : DialogWindow
    {
        internal enum Mode { Paste, Find, Guess, AddRows }

        private readonly Mode _mode;
        private readonly string _deviceName;
        private readonly string _deviceGuid;
        private readonly IReadOnlyList<string> _controllers;
        private readonly bool _shortcuts;
        private List<SdlFindRow> _all = new();
        private readonly Func<IReadOnlyList<LiveItem>> _catalog;

        /// <summary>The catalog IDs of Find's search box and list: the
        /// dialog's name, then the control's.</summary>
        internal const string SearchBoxId = "dialog/SdlMappingDialog/SearchBox";
        internal const string FindListId = "dialog/SdlMappingDialog/FindList";

        /// <summary>The row to apply to the device (Paste, Find, Guess).</summary>
        internal SdlMappingRow PickedRow { get; private set; }

        /// <summary>The rows to keep (Add Rows).</summary>
        internal IReadOnlyList<SdlMappingRow> PickedRows { get; private set; } = Array.Empty<SdlMappingRow>();

        /// <summary>How the dialog's checks read the clipboard and a file,
        /// replaceable in tests.</summary>
        internal static Func<string> ReadClipboard = () =>
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (System.Runtime.InteropServices.COMException) { return null; }
        };

        internal SdlMappingDialog(Mode mode, string deviceName, string deviceGuid, IReadOnlyList<string> controllers, string text = null,
            bool namedByShortcuts = false)
        {
            InitializeComponent();
            _mode = mode;
            _shortcuts = namedByShortcuts;
            _deviceName = deviceName ?? string.Empty;
            _deviceGuid = deviceGuid;
            _controllers = controllers ?? Array.Empty<string>();
            var s = Strings.Instance;
            switch (mode)
            {
                case Mode.Paste:
                    Title = string.Format(CultureInfo.CurrentCulture, s.SdlMappingDialog_PasteTitle_Format, _deviceName);
                    PrimaryButton.Content = s.SdlMappingDialog_Paste;
                    NoteLine.Text = s.SdlMappingDialog_Note;
                    break;
                case Mode.Guess:
                    Title = string.Format(CultureInfo.CurrentCulture, s.SdlMappingDialog_GuessTitle_Format, _deviceName);
                    PrimaryButton.Content = s.Common_Apply;
                    NoteLine.Text = s.SdlMappingDialog_Note;
                    break;
                case Mode.Find:
                    Title = string.Format(CultureInfo.CurrentCulture, s.SdlMappingDialog_FindTitle_Format, _deviceName);
                    PrimaryButton.Content = s.Common_Apply;
                    NoteLine.Text = string.Format(CultureInfo.CurrentCulture, s.SdlMappingDialog_FindNote_Format, _deviceName);
                    FindPart.Visibility = Visibility.Visible;
                    RowBox.Visibility = Visibility.Collapsed;
                    VerdictLine.Visibility = Visibility.Collapsed;
                    // The list and its search box join the catalog while the
                    // dialog is open, so a program picks a row as a person
                    // does (#600 item 48).
                    _catalog = CatalogItems;
                    Loaded += (_, _) => LiveCatalog.AddProvider(_catalog);
                    Closed += (_, _) => LiveCatalog.RemoveProvider(_catalog);
                    break;
                case Mode.AddRows:
                    Title = s.SdlMappingDialog_AddTitle;
                    PrimaryButton.Content = s.Common_Add;
                    NoteLine.Text = s.SdlMappingDialog_AddNote;
                    OpenFileButton.Visibility = Visibility.Visible;
                    break;
            }
            if (mode != Mode.Find)
            {
                // A row on the clipboard is pasted at once, as the board draws
                // it. Only text that passes the check is: the dialog is a
                // program's to read, and the clipboard is the user's.
                string offered = text ?? (mode is Mode.Paste or Mode.AddRows ? Prefill(mode, ReadClipboard(), _deviceGuid) : null);
                RowBox.Text = offered ?? string.Empty;
                Check();
            }
        }

        /// <summary>What the clipboard puts in the box as the dialog opens:
        /// its text when that passes the check, and nothing else.</summary>
        internal static string Prefill(Mode mode, string clipboard, string deviceGuid) =>
            clipboard != null && Verdict(mode, clipboard, deviceGuid, out _, out _).Ok ? clipboard : null;

        /// <summary>What Add Rows opens with: the clipboard's text when it
        /// passes the check, else the rows of the user's own
        /// SDL_GAMECONTROLLERCONFIG variable PadForge does not keep (#600),
        /// else nothing.</summary>
        internal static string AddRowsText(string clipboard, string profileRows) =>
            Prefill(Mode.AddRows, clipboard, null) ?? (string.IsNullOrEmpty(profileRows) ? null : profileRows);

        /// <summary>Opens the dialog over the window and answers whether the
        /// person pressed its primary button.</summary>
        internal static bool Show(SdlMappingDialog dialog, Window owner)
        {
            dialog.Owner = owner;
            return dialog.ShowDialog() == true;
        }

        /// <summary>Find's rows, which the caller reads on the poll thread.</summary>
        internal void SetRows(List<SdlFindRow> rows)
        {
            _all = rows ?? new List<SdlFindRow>();
            Filter();
        }

        // ─────────────────────────────────────────────
        //  The check
        // ─────────────────────────────────────────────

        private void RowBox_TextChanged(object sender, TextChangedEventArgs e) => Check();

        private void Check()
        {
            var verdict = Verdict(_mode, RowBox.Text, _deviceGuid, out var row, out var rows);
            PickedRow = row;
            PickedRows = rows;
            VerdictLead.Text = verdict.Lead;
            VerdictLead.Foreground = (System.Windows.Media.Brush)FindResource(verdict.Ok ? "ColdBrush" : "WaitBrush");
            VerdictDetail.Text = verdict.Detail ?? string.Empty;
            PrimaryButton.IsEnabled = verdict.Ok;
            if (_mode == Mode.AddRows)
            {
                // How many of the rows will be added, and how many will not
                // past the cap (#600).
                var mine = ControllerDatabaseMine.Current;
                ChangeLine.Visibility = verdict.Ok ? Visibility.Visible : Visibility.Collapsed;
                if (verdict.Ok) ChangeLine.Text = AddCountWords(rows.Count, mine.AddCount(rows), mine.Count);
                return;
            }
            ShowChange(verdict.Ok);
        }

        /// <summary>The Add Rows count (#600): every row will be added, or how
        /// many of the user's are kept now and how many of the text's will
        /// and will not be added. A row under an ID kept already replaces it
        /// and counts as added.</summary>
        internal static string AddCountWords(int rows, int added, int kept)
        {
            var culture = CultureInfo.CurrentCulture;
            if (added >= rows)
                return string.Format(culture, Strings.CountFormat(nameof(Strings.SdlMappingDialog_AddAll_Format), rows), rows, ControllerDatabaseMine.MaxRows);
            return string.Format(culture, Strings.CountFormat(nameof(Strings.SdlMappingDialog_AddSome_Format), kept), kept, added, rows - added);
        }

        private void ShowChange(bool show)
        {
            ChangeLine.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) ChangeLine.Text = ChangeWords(_deviceName, _controllers, _shortcuts);
        }

        /// <summary>What applying a row changes, in words: how the device is
        /// read, and which controllers' rows are mapped again.</summary>
        internal static string ChangeWords(string deviceName, IReadOnlyList<string> controllers, bool namedByShortcuts = false)
        {
            var s = Strings.Instance;
            string words = controllers == null || controllers.Count == 0
                ? string.Format(CultureInfo.CurrentCulture, s.SdlMappingDialog_NoController_Format, deviceName)
                : string.Format(CultureInfo.CurrentCulture, s.SdlMappingDialog_Remaps_Format, deviceName, string.Join(", ", controllers));
            return namedByShortcuts
                ? words + " " + string.Format(CultureInfo.CurrentCulture, s.SdlMappingDialog_Shortcuts_Format, deviceName)
                : words;
        }

        internal readonly record struct CheckResult(bool Ok, string Lead, string Detail);

        /// <summary>
        /// The check of the text (#600 item 47), as plain logic: every row in
        /// it is read, rows for other systems are passed over, and for a
        /// device the row SDL would read it through is picked first, then one
        /// of its vendor and product, then the first. Add Rows keeps every
        /// Windows row.
        /// </summary>
        internal static CheckResult Verdict(Mode mode, string text, string deviceGuid, out SdlMappingRow row,
            out IReadOnlyList<SdlMappingRow> rows)
        {
            var s = Strings.Instance;
            row = null;
            var all = SdlMappingRow.ReadAll(text, out int other, out var refusal, out int refused);
            rows = all;
            if (all.Count == 0) return new CheckResult(false, RefusalWords(refusal), null);

            if (mode == Mode.AddRows)
            {
                string found = string.Format(CultureInfo.CurrentCulture, Strings.CountFormat(nameof(Strings.SdlMappingDialog_AddFound_Format), all.Count), all.Count, other);
                string detail = refused == 0 ? null
                    : string.Format(CultureInfo.CurrentCulture, s.SdlMappingDialog_AddRefused_Format, refused, RefusalWords(refusal));
                return new CheckResult(true, found, detail);
            }

            row = SdlMappingRow.PickFor(all, deviceGuid, out var fit);
            string fields = row.FieldCount.ToString(CultureInfo.CurrentCulture);
            string lead = mode == Mode.Guess ? s.SdlMappingDialog_GuessLead : s.SdlMappingDialog_ForThisDevice;
            string detailKey = mode == Mode.Guess ? nameof(Strings.SdlMappingDialog_Detail_Guess_Format)
                : string.IsNullOrEmpty(deviceGuid) ? nameof(Strings.SdlMappingDialog_Detail_PadForge_Format)
                : fit switch
                {
                    SdlMappingRow.Fit.ItsId => nameof(Strings.SdlMappingDialog_Detail_ItsId_Format),
                    SdlMappingRow.Fit.SameProduct => nameof(Strings.SdlMappingDialog_Detail_SameProduct_Format),
                    _ => nameof(Strings.SdlMappingDialog_Detail_Other_Format),
                };
            // A row of one field says field (Strings.OneForms).
            return new CheckResult(true, lead, string.Format(CultureInfo.CurrentCulture, Strings.CountFormat(detailKey, row.FieldCount), fields));
        }

        /// <summary>Why a text was not taken, in words.</summary>
        internal static string RefusalWords(SdlRowRefusal why)
        {
            var s = Strings.Instance;
            return why switch
            {
                SdlRowRefusal.OtherPlatform => s.SdlMappingDialog_Refused_OtherPlatform,
                SdlRowRefusal.BadId => s.SdlMappingDialog_Refused_BadId,
                SdlRowRefusal.BadName => s.SdlMappingDialog_Refused_BadName,
                SdlRowRefusal.Hint => s.SdlMappingDialog_Refused_Hint,
                SdlRowRefusal.UnknownField => s.SdlMappingDialog_Refused_UnknownField,
                SdlRowRefusal.BadBinding => s.SdlMappingDialog_Refused_BadBinding,
                SdlRowRefusal.RepeatedField => s.SdlMappingDialog_Refused_RepeatedField,
                SdlRowRefusal.TooLong => s.SdlMappingDialog_Refused_TooLong,
                SdlRowRefusal.NoBindings => s.SdlMappingDialog_Refused_NoBindings,
                SdlRowRefusal.NotARow => s.SdlMappingDialog_Refused_NotARow,
                _ => s.SdlMappingDialog_Refused_Empty,
            };
        }

        // ─────────────────────────────────────────────
        //  Find
        // ─────────────────────────────────────────────

        private void Search_TextChanged(object sender, TextChangedEventArgs e) => Filter();

        private void Filter()
        {
            var shown = Matching(_all, SearchBox.Text);
            FindList.ItemsSource = shown;
            FindEmpty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (shown.Count > 0) FindList.SelectedIndex = 0;
            else PickFound(null);
        }

        /// <summary>The rows whose name, ID or VID:PID holds every word typed,
        /// the device's own first as the list holds them.</summary>
        internal static List<SdlFindRow> Matching(IReadOnlyList<SdlFindRow> rows, string typed)
        {
            string[] words = (typed ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var list = new List<SdlFindRow>();
            foreach (var r in rows ?? Array.Empty<SdlFindRow>())
            {
                string ids = SdlMappingRow.TryReadIds(r.Row.Guid, out ushort v, out ushort p)
                    ? v.ToString("x4", CultureInfo.InvariantCulture) + ":" + p.ToString("x4", CultureInfo.InvariantCulture)
                    : string.Empty;
                bool all = true;
                foreach (string w in words)
                    if (r.Name.IndexOf(w, StringComparison.CurrentCultureIgnoreCase) < 0
                        && r.Row.Guid.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0
                        && ids.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        all = false;
                        break;
                    }
                if (all) list.Add(r);
            }
            return list;
        }

        private void FindList_SelectionChanged(object sender, SelectionChangedEventArgs e) => PickFound(FindList.SelectedItem as SdlFindRow);

        private void PickFound(SdlFindRow found)
        {
            PickedRow = found?.Row;
            PrimaryButton.IsEnabled = found != null;
            ShowChange(found != null);
        }

        private void FindList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FindList.SelectedItem is SdlFindRow) DialogResult = true;
        }

        /// <summary>
        /// Find's search box and list as catalog items, at the class the
        /// dialog gives them: set types the words the list is narrowed by,
        /// or picks a row as a click picks it, by the words a screen reader
        /// says for it (its name, then where it comes from) or by its place
        /// from 0. Apply is the dialog's own button, pressed by its ID.
        /// </summary>
        private IReadOnlyList<LiveItem> CatalogItems()
        {
            var s = Strings.Instance;
            var (searchClass, searchWhy) = Reach.Of(SearchBox);
            var (listClass, listWhy) = Reach.Of(FindList);
            return new[]
            {
                new LiveItem(SearchBoxId, LiveKind.Setting, s.SdlMappingDialog_Search, searchClass, searchWhy)
                {
                    Where = Title,
                    Takes = "text",
                    Element = () => SearchBox,
                    Read = () => SearchBox.Text,
                    Write = text => SearchBox.Text = text ?? string.Empty,
                },
                new LiveItem(FindListId, LiveKind.Setting, s.SdlMappingDialog_FindList, listClass, listWhy)
                {
                    Where = Title,
                    Takes = "list",
                    Element = () => FindList,
                    Read = () => (FindList.SelectedItem as SdlFindRow)?.ReadName,
                    Write = Pick,
                    Choices = () => FindList.Items.OfType<SdlFindRow>().Select(r => r.ReadName).ToList(),
                },
            };
        }

        /// <summary>Picks a row of the list as a click does: by its words, the
        /// screen reader's or the two lines it shows, or by its place from 0.
        /// Words that two rows share pick neither.</summary>
        internal void Pick(string text)
        {
            var rows = FindList.Items.OfType<SdlFindRow>().ToList();
            string want = (text ?? string.Empty).Trim();
            if (rows.Count == 0) throw new ArgumentException("The list shows no row. Empty the search box to see every row.");
            int at = -1;
            if (int.TryParse(want, NumberStyles.None, CultureInfo.InvariantCulture, out int place))
            {
                if (place >= rows.Count) throw new ArgumentException("The list holds " + rows.Count + " rows, from 0 to " + (rows.Count - 1) + ".");
                at = place;
            }
            else
            {
                var same = new List<int>();
                for (int i = 0; i < rows.Count; i++)
                    if (string.Equals(rows[i].ReadName, want, StringComparison.CurrentCultureIgnoreCase)
                        || string.Equals(rows[i].Name + " " + rows[i].Detail, want, StringComparison.CurrentCultureIgnoreCase))
                        same.Add(i);
                if (same.Count > 1)
                    throw new ArgumentException("Rows " + string.Join(", ", same) + " read the same. Give the row's place from 0, or narrow the list by its ID in the search box.");
                if (same.Count == 1) at = same[0];
            }
            if (at < 0) throw new ArgumentException("No row of the list reads \"" + want + "\". A read of the dialog gives its rows, and the search box narrows them.");
            FindList.SelectedIndex = at;
            FindList.ScrollIntoView(FindList.SelectedItem);
        }

        // ─────────────────────────────────────────────
        //  Buttons
        // ─────────────────────────────────────────────

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Filter = Strings.Instance.SdlMappingDialog_FileFilter + "|*.txt|*.*|*.*",
                CheckFileExists = true,
            };
            if (picker.ShowDialog(this) != true) return;
            try
            {
                var info = new System.IO.FileInfo(picker.FileName);
                // The community's whole file is about 0.6 MB.
                if (info.Length > ControllerDatabaseDownload.MaxBytes)
                {
                    VerdictLead.Text = Strings.Instance.SdlMappingDialog_FileTooLarge;
                    VerdictDetail.Text = string.Empty;
                    PrimaryButton.IsEnabled = false;
                    return;
                }
                RowBox.Text = System.IO.File.ReadAllText(picker.FileName);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                VerdictLead.Text = ex.Message;
                VerdictDetail.Text = string.Empty;
                PrimaryButton.IsEnabled = false;
            }
        }

        private void Primary_Click(object sender, RoutedEventArgs e)
        {
            if (_mode == Mode.AddRows ? PickedRows.Count == 0 : PickedRow == null) return;
            DialogResult = true;
        }
    }
}
