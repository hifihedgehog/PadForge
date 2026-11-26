using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PadForge.Common;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Home's card while PadForge runs with rights from a folder other
    /// programs can change (#597): it stands until the move, or until the
    /// folder is PadForge's own.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class HomeSharedFolderTests : IDisposable
    {
        private const string Exe = @"C:\Users\sonic\Downloads\PadForge.exe";

        public void Dispose()
        {
            ExeFolderState.CheckSeam = _ => ExeFolder.Standing.Own;
            ExeFolderState.Instance.Recheck();
            ExeFolderState.CheckSeam = null;
        }

        [Fact]
        public void TheCard_StandsWhileTheFolderIsShared_AndGoesOnceItIsPadForgesOwn()
        {
            ExeFolderState.CheckSeam = _ => ExeFolder.Standing.Shared;
            var state = ExeFolderState.Instance;
            state.Start(Exe);
            Assert.True(state.IsShared);
            Assert.True(state.CanMove);
            Assert.Equal(string.Format(PadForge.Resources.Strings.Strings.Instance.Home_SharedFolderOthers_Format, @"C:\Users\sonic\Downloads"), state.HomeCardText);
            Assert.Contains(@"C:\Users\sonic\Downloads", state.HomeCardText, StringComparison.Ordinal);
            // A judgment that still finds other files keeps the card.
            state.Recheck();
            Assert.True(state.IsShared);
            // The other files left: the folder is judged PadForge's own.
            ExeFolderState.CheckSeam = _ => ExeFolder.Standing.Own;
            state.Recheck();
            Assert.False(state.IsShared);
            Assert.False(state.CanMove);
        }

        /// <summary>The folder lines and the move's error are formatted in
        /// code, so they follow a language change only when the state raises
        /// them again. The line for the logon task's removal follows too.</summary>
        [Fact]
        public void TheFolderLines_FollowALanguageChange()
        {
            ExeFolderState.CheckSeam = _ => ExeFolder.Standing.Shared;
            var state = ExeFolderState.Instance;
            state.Start(Exe);
            state.MoveFailed(new ExeFolder.MoveResult(null, "Access is denied."));
            var raised = new System.Collections.Generic.List<string>();
            void Note(object sender, System.ComponentModel.PropertyChangedEventArgs e) => raised.Add(e.PropertyName);
            state.PropertyChanged += Note;
            try
            {
                PadForge.Resources.Strings.Strings.ChangeCulture(System.Globalization.CultureInfo.CurrentUICulture);
            }
            finally
            {
                state.PropertyChanged -= Note;
            }
            Assert.Contains(nameof(ExeFolderState.RunsFromText), raised);
            Assert.Contains(nameof(ExeFolderState.HomeCardText), raised);
            Assert.Contains(nameof(ExeFolderState.MoveError), raised);
            Assert.Contains(nameof(ExeFolderState.StartAtLoginTurnedOffText), raised);
        }

        /// <summary>A start removes the logon task for more than one cause,
        /// and the line under Start at Login says the one it read: a task it
        /// could not read, an exe that is gone, an own folder it could not
        /// protect, or a folder that holds other files.</summary>
        [Fact]
        public void ARemovedLogonTask_IsSaidWithTheCauseTheStartRead()
        {
            string dir = Path.Combine(UpdateService.TestScratchRoot, "logon-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string here = Path.Combine(dir, "PadForge.exe");
                File.WriteAllText(here, "x");
                static string Definition(string exe)
                    => "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
                       + "<Principals><Principal><UserId>S-1-5-21-1-2-3-1001</UserId></Principal></Principals>"
                       + "<Actions><Exec><Command>\"" + exe + "\"</Command></Exec></Actions></Task>";
                Assert.Equal(StartupHelper.TaskRemoval.Unreadable, StartupHelper.WhyRemove(null, _ => ExeFolder.Standing.Own));
                Assert.Equal(StartupHelper.TaskRemoval.ExeGone,
                    StartupHelper.WhyRemove(Definition(Path.Combine(dir, "moved", "PadForge.exe")), _ => ExeFolder.Standing.Shared));
                Assert.Equal(StartupHelper.TaskRemoval.Shared, StartupHelper.WhyRemove(Definition(here), _ => ExeFolder.Standing.Shared));
                Assert.Equal(StartupHelper.TaskRemoval.Unprotected, StartupHelper.WhyRemove(Definition(here), _ => ExeFolder.Standing.Unprotected));
                // A folder that could not be listed is no folder that holds
                // other files: nothing in it was read.
                Assert.Equal(StartupHelper.TaskRemoval.FolderUnread, StartupHelper.WhyRemove(Definition(here), _ => ExeFolder.Standing.Unread));
                Assert.Equal(StartupHelper.TaskRemoval.None, StartupHelper.WhyRemove(Definition(here), _ => ExeFolder.Standing.Own));

                var lines = new[]
                {
                    StartupHelper.TaskRemoval.Shared, StartupHelper.TaskRemoval.ExeGone,
                    StartupHelper.TaskRemoval.Unprotected, StartupHelper.TaskRemoval.Unreadable,
                    StartupHelper.TaskRemoval.FolderUnread,
                }.Select(ExeFolderState.LineFor).ToArray();
                Assert.Equal(lines.Length, lines.Distinct().Count());
                Assert.Equal(PadForge.Resources.Strings.Strings.Instance.ExeFolder_StartAtLoginTurnedOff, lines[0]);
                Assert.Equal(PadForge.Resources.Strings.Strings.Instance.ExeFolder_StartAtLoginTurnedOff_FolderUnread, lines[4]);
                Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line)));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        /// <summary>A folder that holds other files is watched, and judged
        /// again once it has been quiet.</summary>
        [Fact]
        public void ASharedFolder_IsWatched_AndJudgedAgainWhenItIsQuiet()
        {
            string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "ExeFolderState.cs");
            string start = FenceSource.Body(source, "internal void Start(string exe)");
            Assert.Contains("if (IsShared && CheckSeam == null) Watch(exe);", start, StringComparison.Ordinal);
            string watch = FenceSource.Body(source, "private void Watch(string exe)");
            Assert.Contains("watcher.Created += Soon;", watch, StringComparison.Ordinal);
            Assert.Contains("watcher.Deleted += Soon;", watch, StringComparison.Ordinal);
            Assert.Contains("_recheck?.Change(RecheckQuietMs, System.Threading.Timeout.Infinite)", watch, StringComparison.Ordinal);
            string recheck = FenceSource.Body(source, "internal void Recheck()");
            Assert.Contains("StopWatching();", recheck, StringComparison.Ordinal);
        }

        /// <summary>The card is the offer's band under it: the shield, the
        /// line and the move, the move Settings and Devices offer.</summary>
        [Fact]
        public void TheCard_SitsUnderTheOffer_WithTheMoveAsItsOneAction()
        {
            string page = AuditDelta20261002Tests.RepoText("PadForge.App", "Views", "HomePage.xaml");
            int offer = page.IndexOf("<Border x:Name=\"HomeOffer\"", StringComparison.Ordinal);
            int card = page.IndexOf("<Border x:Name=\"HomeSharedFolder\"", StringComparison.Ordinal);
            int controllers = page.IndexOf("<TextBlock x:Name=\"ControllersHeading\"", StringComparison.Ordinal);
            Assert.True(offer > 0 && card > offer && controllers > card);
            string block = page.Substring(card, controllers - card);
            Assert.Contains("Visibility=\"{Binding IsShared, Source={x:Static services:ExeFolderState.Instance}", block, StringComparison.Ordinal);
            Assert.Contains("Click=\"SharedFolderMove_Click\"", block, StringComparison.Ordinal);
            Assert.Contains("IsEnabled=\"{Binding CanMove, Source={x:Static services:ExeFolderState.Instance}}\"", block, StringComparison.Ordinal);
            Assert.Contains("Data=\"{StaticResource ShieldLineGeometry}\"", block, StringComparison.Ordinal);
            Assert.Contains("AutomationProperties.AutomationId=\"HomeSharedFolder\"", block, StringComparison.Ordinal);
            string code = AuditDelta20261002Tests.RepoText("PadForge.App", "Views", "HomePage.xaml.cs");
            Assert.Contains("=> PadForge.Services.ExeFolderState.Instance.RequestMove();", FenceSource.Body(code, "private void SharedFolderMove_Click(object sender, RoutedEventArgs e)"), StringComparison.Ordinal);
        }

        /// <summary>Laid out on Home with the app's resources: shown with its
        /// line and button while shared, collapsed once the folder is
        /// PadForge's own.</summary>
        [Fact]
        public void TheCard_IsLaidOutOnHome()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                ExeFolderState.CheckSeam = _ => ExeFolder.Standing.Shared;
                ExeFolderState.Instance.Start(Exe);
                var page = new HomePage { DataContext = new DashboardViewModel { RemoteLink = new SettingsViewModel() } };
                page.Measure(new Size(1176, 1068));
                page.Arrange(new Rect(0, 0, 1176, 1068));
                page.UpdateLayout();
                var card = (FrameworkElement)page.FindName("HomeSharedFolder");
                var line = (TextBlock)page.FindName("HomeSharedFolderLine");
                var move = (FrameworkElement)page.FindName("HomeSharedFolderMove");
                Assert.Equal(Visibility.Visible, card.Visibility);
                Assert.True(card.ActualHeight > 30 && card.ActualWidth > 600, card.ActualWidth + " x " + card.ActualHeight);
                Assert.Equal(ExeFolderState.Instance.HomeCardText, line.Text);
                Assert.True(move.ActualWidth > 0 && move.IsEnabled);
                ExeFolderState.CheckSeam = _ => ExeFolder.Standing.Own;
                ExeFolderState.Instance.Recheck();
                page.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, card.Visibility);
            }, TimeSpan.FromMinutes(2), "Home did not lay out in time.");
        }
    }
}
