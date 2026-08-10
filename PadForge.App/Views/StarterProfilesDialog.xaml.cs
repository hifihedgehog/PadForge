using System.Windows;
using System.Windows.Input;
using PadForge.Services;

namespace PadForge.Views
{
    /// <summary>
    /// The starter-profile gallery (#256). Pick an archetype, save it, and an
    /// ordinary editable profile lands in the list, exactly as a community
    /// config import does. Nothing here is locked or special-cased after the
    /// save: the whole point is that the user owns what they get.
    /// </summary>
    public partial class StarterProfilesDialog
    {
        /// <summary>Set by the caller. Receives the built profile and returns
        /// the deduped display name it was registered under. Shared with the
        /// Workshop browse path so both land through identical steps.</summary>
        public System.Func<ProfileData, bool, string> SaveSink { get; set; }

        /// <summary>Display name of the profile saved this session, or null
        /// when the dialog was dismissed without saving.</summary>
        public string SavedProfileName { get; private set; }

        public StarterProfilesDialog()
        {
            InitializeComponent();

            // ExtendsContentIntoTitleBar sets WindowChrome.CaptionHeight to 0,
            // so without this no point in the window is non-client and the
            // dialog cannot be moved at all. Same wiring as the sibling
            // dialogs; AuditRound35FixTests guards the whole family.
            MouseLeftButtonDown += (_, __) => { try { DragMove(); } catch { } };

            StarterList.ItemsSource = StarterProfileCatalog.All;
            if (StarterList.Items.Count > 0) StarterList.SelectedIndex = 0;
        }

        private void Save_Click(object sender, RoutedEventArgs e) => SaveSelected();

        private void StarterList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Only the row itself, never the empty space below the last item.
            if (StarterList.SelectedItem != null) SaveSelected();
        }

        private void SaveSelected()
        {
            if (StarterList.SelectedItem is not StarterProfileInfo info) return;
            if (SaveSink == null) { DialogResult = false; return; }

            // Build fresh on every save so two saves never share mutable
            // state, and so a second save of the same archetype is a genuinely
            // independent profile rather than an alias of the first.
            SavedProfileName = SaveSink(info.Build(), false);
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void HeaderClose_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
