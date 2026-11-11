using System.IO;
using System.Reflection;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Which cards are open is interface state the settings file keeps
    /// (#574). Opening a card saved it like a setting, and two seconds
    /// later the message line read "Settings saved to PadForge.xml." for
    /// every card a user opened (seen 2026-11-11). The autosave that writes
    /// interface state alone now raises no message. A setting that changes
    /// before that save makes it an ordinary one.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ViewStateSaveTests
    {
        private static (MainViewModel vm, SettingsService settings) Fresh()
        {
            var vm = new MainViewModel();
            var settings = new SettingsService(vm);
            Assert.False(settings.IsDirty);
            Assert.False(settings.OnlyViewStateIsDirty);
            return (vm, settings);
        }

        [Fact]
        public void ACardOpenedIsSavedAndQuiet()
        {
            var (_, settings) = Fresh();
            settings.MarkViewStateDirty();
            Assert.True(settings.IsDirty);
            Assert.True(settings.OnlyViewStateIsDirty);
            // A second card is still interface state alone.
            settings.MarkViewStateDirty();
            Assert.True(settings.OnlyViewStateIsDirty);
        }

        [Fact]
        public void ASettingChangedAfterACardOpenedMakesTheSaveOrdinary()
        {
            var (_, settings) = Fresh();
            settings.MarkViewStateDirty();
            settings.MarkDirty();
            Assert.True(settings.IsDirty);
            Assert.False(settings.OnlyViewStateIsDirty);
        }

        [Fact]
        public void ACardOpenedAfterASettingChangedLeavesTheSaveOrdinary()
        {
            var (_, settings) = Fresh();
            settings.MarkDirty();
            settings.MarkViewStateDirty();
            Assert.True(settings.IsDirty);
            Assert.False(settings.OnlyViewStateIsDirty);
        }

        /// <summary>A save clears the mark, and a save asked for directly
        /// says so on the message line whatever was waiting.</summary>
        [Fact]
        public void ASaveClearsTheMark_AndADirectSaveStillSaysSo()
        {
            var (vm, settings) = Fresh();
            string path = Path.Combine(Path.GetTempPath(), "PadForgeViewState_" + System.Guid.NewGuid().ToString("N") + ".xml");
            typeof(SettingsService).GetField("_settingsFilePath", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(settings, path);
            try
            {
                settings.MarkViewStateDirty();
                Assert.True(settings.Save());
                Assert.False(settings.IsDirty);
                Assert.False(settings.OnlyViewStateIsDirty);
                Assert.Contains(Path.GetFileName(path), vm.StatusText);

                // The next card opened is quiet again.
                settings.MarkViewStateDirty();
                Assert.True(settings.OnlyViewStateIsDirty);
            }
            finally { File.Delete(path); }
        }

        /// <summary>The wiring: a card's change is marked as interface
        /// state, the autosave is the one save that can be quiet, and the
        /// save itself asks before it writes the message.</summary>
        [Fact]
        public void TheAutosaveIsTheQuietOne()
        {
            string window = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("CardFoldState.Changed += (_, _) => _settingsService.MarkViewStateDirty();", window);

            string service = ExtrasPageTests.RepoText("PadForge.App", "Services", "SettingsService.cs");
            Assert.Matches(@"_announceSave = !_onlyViewStateIsDirty;\s*try \{ Save\(\); \}\s*finally \{ _announceSave = true; \}", service);
            Assert.Matches(@"if \(_announceSave\)\s*_mainVm\.StatusText = string\.Format\(Strings\.Instance\.Status_SettingsSaved_Format, Path\.GetFileName\(filePath\)\);", service);
            Assert.Matches(@"_onlyViewStateIsDirty = viewState && \(!IsDirty \|\| _onlyViewStateIsDirty\);", service);
        }
    }
}
