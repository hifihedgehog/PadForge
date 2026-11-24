using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
            Assert.Equal(string.Format(PadForge.Resources.Strings.Strings.Instance.Home_SharedFolder_Format, @"C:\Users\sonic\Downloads"), state.HomeCardText);
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
