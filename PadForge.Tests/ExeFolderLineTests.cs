using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The line Settings and a touchpad's row show while PadForge's folder
    /// is not safe for a task with rights (#597): a folder that holds other
    /// files says so, and a folder of PadForge's own whose access list could
    /// not be made sound, or that PadForge could not read, says PadForge
    /// could not keep other programs from changing it. Neither line states
    /// what the code did not read.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class ExeFolderLineTests : IDisposable
    {
        private const string Exe = @"C:\Users\Public\Downloads\PadForge.exe";

        public void Dispose()
        {
            ExeFolderState.CheckSeam = _ => ExeFolder.Standing.Own;
            ExeFolderState.Instance.Recheck();
            ExeFolderState.CheckSeam = null;
        }

        /// <summary>A folder Read cannot list is no folder that holds other
        /// files: nothing in it was read.</summary>
        [Fact]
        public void AFolderPadForgeCannotRead_IsUnread()
        {
            string missing = Path.Combine(UpdateService.TestScratchRoot, "exefolder-" + Guid.NewGuid().ToString("N"), "PadForge.exe");
            Assert.Null(ExeFolder.Read(Path.GetDirectoryName(missing)));
            Assert.Equal(ExeFolder.Standing.Unread, ExeFolder.Check(missing));
            Assert.False(ExeFolder.MayHoldTasks(missing));
        }

        /// <summary>Home's card says what the judgment read: other files in
        /// the folder, a folder of PadForge's own other accounts may change
        /// (OthersMayChange read it), or a folder PadForge could not read.
        /// Only the second states that other programs can change it.</summary>
        [Theory]
        [InlineData("Shared")]
        [InlineData("Unprotected")]
        [InlineData("Unread")]
        public void HomesCard_StatesOnlyWhatTheFolderWasJudged(string judged)
        {
            var standing = Enum.Parse<ExeFolder.Standing>(judged);
            ExeFolderState.CheckSeam = _ => standing;
            ExeFolderState.Instance.Start(Exe);
            var s = Strings.Instance;
            string format = standing switch
            {
                ExeFolder.Standing.Shared => s.Home_SharedFolderOthers_Format,
                ExeFolder.Standing.Unread => s.Home_SharedFolderUnread_Format,
                _ => s.Home_SharedFolder_Format,
            };
            Assert.Equal(string.Format(format, @"C:\Users\Public\Downloads"), ExeFolderState.Instance.HomeCardText);
        }

        /// <summary>Whether an element and every element above it on the
        /// page is visible, as the page lays them out.</summary>
        private static bool Shown(FrameworkElement e, FrameworkElement page)
        {
            for (DependencyObject at = e; at != null && at != page; at = LogicalTreeHelper.GetParent(at))
                if (at is UIElement { Visibility: not Visibility.Visible }) return false;
            return true;
        }

        private static TextBlock LineOf(FrameworkElement page, string text)
            => Assert.Single(PadForge.Search.SearchLocator.LogicalDescendants(page).OfType<TextBlock>(), t => t.Text == text);

        [Theory]
        [InlineData("Shared", true)]
        [InlineData("Unprotected", false)]
        [InlineData("Unread", false)]
        public void SettingsShowsTheLineOfWhatWasRead(string judged, bool holdsOtherFiles)
        {
            var standing = Enum.Parse<ExeFolder.Standing>(judged);
            ExeFolderState.CheckSeam = _ => standing;
            ExeFolderState.Instance.Start(Exe);
            Assert.True(ExeFolderState.Instance.IsShared);
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var page = new SettingsPage { DataContext = new SettingsViewModel() };
                var window = new Window { Width = 1280, Height = 900, ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, Content = page };
                try
                {
                    window.Show();
                    PageRenderDump.Settle(dispatcher);
                    var others = LineOf(page, Strings.Instance.ExeFolder_NeedsOwnFolder);
                    Assert.Equal(holdsOtherFiles, Shown(others, page));
                    var unsafeLine = PadForge.Search.SearchLocator.LogicalDescendants(page).OfType<TextBlock>()
                        .SingleOrDefault(t => t.Text == Strings.Instance.ExeFolder_CouldNotProtect);
                    Assert.True(holdsOtherFiles ? unsafeLine == null || !Shown(unsafeLine, page) : unsafeLine != null && Shown(unsafeLine, page),
                        standing + ": the line that PadForge could not keep other programs from changing its folder");
                }
                finally
                {
                    window.Close();
                }
            }, TimeSpan.FromMinutes(3), "The folder line test timed out.");
        }
    }
}
