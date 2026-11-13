using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>What became of one profile file: the message line's text when
    /// it landed, a reason when it was refused, both null when the user
    /// canceled the Import Profile report.</summary>
    internal readonly record struct ProfileFileResult(string StatusText, string Problem);

    /// <summary>
    /// Get Profiles (#574): a starter profile, a community config or a profile
    /// file from another tool, in one window of three tabs. Whatever lands is
    /// an ordinary profile the user can edit, registered through the sinks
    /// MainWindow hands the window, and StatusText says what came in.
    /// </summary>
    public partial class GetProfilesDialog
    {
        private readonly SettingsViewModel _settings;
        private GetProfilesTab _tab;
        private bool _communityAttached;

        public GetProfilesDialog(SettingsViewModel settings, GetProfilesTab tab)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            InitializeComponent();

            StarterList.ItemsSource = StarterProfileCatalog.All;
            if (StarterList.Items.Count > 0) StarterList.SelectedIndex = 0;

            CommunityPanel.StateChanged += (_, _) => OnCommunityStateChanged();
            CommunityPanel.CanSaveChanged += (_, _) => UpdateFoot();

            // The tab's size before the window shows, so CenterOwner centers
            // that size. The tab's radio raises Checked, which only records
            // the tab until the window has loaded.
            _tab = tab;
            ApplySize(tab, recenter: false);
            RadioFor(tab).IsChecked = true;
            Loaded += (_, _) => ShowTab(_tab);
            Closing += (_, _) => CommunityPanel.Shutdown();
        }

        /// <summary>MainWindow.AddWorkshopProfile: registers a profile under a
        /// free name and returns that name. True also loads it.</summary>
        internal Func<ProfileData, bool, string> ProfileSink { get; set; }

        /// <summary>MainWindow.ImportProfileFile: one file read and registered,
        /// with this window as the Import Profile report's owner.</summary>
        internal Func<string, Window, ProfileFileResult> FileImporter { get; set; }

        /// <summary>The message line's text for what landed, null for nothing.</summary>
        internal string StatusText { get; private set; }

        /// <summary>A tab's window: Starters and Import a File as the Dialogs
        /// board draws them, Community as the large board does.</summary>
        internal static (double Width, double Height, double MinWidth, double MinHeight) SizeFor(GetProfilesTab tab)
            => tab == GetProfilesTab.Community ? (1100, 780, 900, 640) : (680, 568, 560, 480);

        private RadioButton RadioFor(GetProfilesTab tab) => tab switch
        {
            GetProfilesTab.Community => CommunityTab,
            GetProfilesTab.File => FileTab,
            _ => StartersTab,
        };

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse(tag, out GetProfilesTab tab)) return;
            _tab = tab;
            if (IsLoaded) ShowTab(tab);
        }

        /// <summary>Shows one tab: its body, its buttons and its size. The
        /// first show of Community hands the panel its sink and its settings,
        /// so opening Get Profiles reaches no network until that tab is
        /// chosen.</summary>
        private void ShowTab(GetProfilesTab tab)
        {
            StartersBody.Visibility = tab == GetProfilesTab.Starters ? Visibility.Visible : Visibility.Collapsed;
            CommunityPanel.Visibility = tab == GetProfilesTab.Community ? Visibility.Visible : Visibility.Collapsed;
            FileBody.Visibility = tab == GetProfilesTab.File ? Visibility.Visible : Visibility.Collapsed;

            if (tab == GetProfilesTab.Community && !_communityAttached)
            {
                _communityAttached = true;
                CommunityPanel.ImportSink = ProfileSink;
                CommunityPanel.Attach(_settings);
            }
            UpdateSearchField();
            UpdateFoot();
            ApplySize(tab, recenter: true);

            switch (tab)
            {
                case GetProfilesTab.Starters:
                    (StarterList.ItemContainerGenerator.ContainerFromItem(StarterList.SelectedItem) as ListBoxItem ?? (UIElement)StarterList).Focus();
                    break;
                case GetProfilesTab.Community:
                    if (CommunityPanel.IsCold) CommunityPanel.FocusStart();
                    else SearchBox.Focus();
                    break;
                default:
                    FileDropZone.Focus();
                    break;
            }
        }

        /// <summary>The search box shows on Community while the panel is not cold.</summary>
        private void UpdateSearchField()
            => SearchField.Visibility = _tab == GetProfilesTab.Community && !CommunityPanel.IsCold
                ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>The foot's buttons follow the tab: Save Profile on Starters
        /// and Community, Save and Apply on Community, Import... on Import a
        /// File. Saving waits for something to save.</summary>
        private void UpdateFoot()
        {
            SaveButton.Visibility = _tab == GetProfilesTab.File ? Visibility.Collapsed : Visibility.Visible;
            SaveApplyButton.Visibility = _tab == GetProfilesTab.Community ? Visibility.Visible : Visibility.Collapsed;
            ImportButton.Visibility = _tab == GetProfilesTab.File ? Visibility.Visible : Visibility.Collapsed;
            SaveButton.IsEnabled = _tab == GetProfilesTab.Community ? CommunityPanel.CanSave : StarterList.SelectedItem != null;
            SaveApplyButton.IsEnabled = CommunityPanel.CanSave;
        }

        private void OnCommunityStateChanged()
        {
            UpdateSearchField();
            UpdateFoot();
            // The gate opened, or Search Games went back to the games: the
            // box holds the focus there.
            if (_tab == GetProfilesTab.Community && IsLoaded && CommunityPanel.ShowsGames)
                SearchBox.Focus();
        }

        /// <summary>Gives the window the tab's size, held inside 95% of the
        /// work area. Once shown, a change of size re-centers the window on
        /// its owner, inside the work area.</summary>
        private void ApplySize(GetProfilesTab tab, bool recenter)
        {
            var size = SizeFor(tab);
            Rect area = SystemParameters.WorkArea;
            double width = Math.Min(size.Width, area.Width * 0.95);
            double height = Math.Min(size.Height, area.Height * 0.95);
            MinWidth = Math.Min(size.MinWidth, width);
            MinHeight = Math.Min(size.MinHeight, height);
            if (Math.Abs(Width - width) < 0.5 && Math.Abs(Height - height) < 0.5) return;
            Width = width;
            Height = height;
            if (!recenter) return;
            Rect over = Owner is { } owner
                ? owner.WindowState == WindowState.Maximized
                    ? area
                    : new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight)
                : area;
            double left = over.Left + (over.Width - width) / 2;
            double top = over.Top + (over.Height - height) / 2;
            Left = Math.Max(area.Left, Math.Min(left, area.Right - width));
            Top = Math.Max(area.Top, Math.Min(top, area.Bottom - height));
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // ─────────────────────────────────────────────
        //  Starters
        // ─────────────────────────────────────────────

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_tab == GetProfilesTab.Community) SaveCommunity(applyAfter: false);
            else SaveStarter();
        }

        private void SaveApply_Click(object sender, RoutedEventArgs e) => SaveCommunity(applyAfter: true);

        private void StarterList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFoot();

        private void StarterList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Only a row, never the empty space below the last one.
            if (ItemsControl.ContainerFromElement(StarterList, e.OriginalSource as DependencyObject) is ListBoxItem)
                SaveStarter();
        }

        private void StarterList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || StarterList.SelectedItem == null) return;
            SaveStarter();
            e.Handled = true;
        }

        /// <summary>Saves the picked starter as an ordinary profile. It is built
        /// fresh on every save, so two saves never share mutable state and a
        /// second save of the same starter is a profile of its own.</summary>
        private void SaveStarter()
        {
            if (StarterList.SelectedItem is not StarterProfileInfo info || ProfileSink == null) return;
            StatusText = string.Format(Strings.Instance.Status_StarterImported_Format, ProfileSink(info.Build(), false));
            DialogResult = true;
        }

        // ─────────────────────────────────────────────
        //  Community
        // ─────────────────────────────────────────────

        private void SaveCommunity(bool applyAfter)
        {
            if (!CommunityPanel.TrySave(applyAfter, out string status)) return;
            StatusText = status;
            DialogResult = true;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            CommunityPanel.SearchTextChanged(SearchBox.Text);
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (CommunityPanel.SearchKeyDown(e.Key)) e.Handled = true;
        }

        // ─────────────────────────────────────────────
        //  Import a File
        // ─────────────────────────────────────────────

        private void Import_Click(object sender, RoutedEventArgs e) => ChooseFile();

        private void FileDropZone_Click(object sender, RoutedEventArgs e) => ChooseFile();

        /// <summary>The Open dialog of profile files: PadForge's, reWASD's and
        /// DS4Windows', the three together first, with the folders the other
        /// tools keep their profiles in as places.</summary>
        private void ChooseFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Strings.Instance.Profiles_Import,
                Filter = $"{Strings.Instance.Profiles_ImportFilterAll} (*{PadForge.Common.ProfileTransfer.FileExtension};*.rewasd;*.xml)|*{PadForge.Common.ProfileTransfer.FileExtension};*.rewasd;*.xml"
                    + "|" + string.Format(Strings.Instance.FileDialog_ProfileFilter_Format, PadForge.Common.ProfileTransfer.FileExtension)
                    + "|" + Strings.Instance.FileDialog_ForeignProfileFilter
                    + "|" + Strings.Instance.FileDialog_AllFilesFilter,
                CheckFileExists = true,
            };
            foreach (string folder in PadForge.Common.ConfigImport.ForeignConfigImporter.ProfileFolders())
                dlg.CustomPlaces.Add(new Microsoft.Win32.FileDialogCustomPlace(folder));
            if (dlg.ShowDialog(this) != true) return;
            if (ImportFile(dlg.FileName)) DialogResult = true;
        }

        /// <summary>Reads one file. A refused file says why under the drop zone
        /// and keeps the window open, and so does a canceled report. True when
        /// the file landed.</summary>
        private bool ImportFile(string path)
        {
            if (FileImporter == null) return false;
            var result = FileImporter(path, this);
            if (!string.IsNullOrEmpty(result.Problem))
            {
                FileProblemText.Text = result.Problem;
                FileProblemText.Visibility = Visibility.Visible;
                return false;
            }
            FileProblemText.Visibility = Visibility.Collapsed;
            if (string.IsNullOrEmpty(result.StatusText)) return false;
            StatusText = result.StatusText;
            return true;
        }

        private void FileDropZone_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = ProfilesPage.IsProfileFileDrag(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>A drop reads each file the page's drop would take, stops at
        /// the first one refused, and closes when any landed.</summary>
        private void FileDropZone_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            bool landed = false;
            foreach (string file in files.Where(ProfilesPage.IsImportableFile))
            {
                if (ImportFile(file)) landed = true;
                else if (FileProblemText.Visibility == Visibility.Visible) break;
            }
            if (landed) DialogResult = true;
        }
    }
}
