using System;
using System.Threading;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Opening a controller's page copies its stored rows into the page's
    /// view. That reload marked the settings changed once for each setter it
    /// ran, so every visit wrote the settings file two seconds later and
    /// raised the saved message, which the shell now shows over the page
    /// (#574). A trace on 2026-11-11 counted 132 marks for one visit to a
    /// page of 22 rows, every one from the reload. The reload now runs
    /// inside <see cref="SettingsService.MirrorOnly"/>, which ignores the
    /// marks it makes.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MirrorOnlyReloadTests
    {
        private static SettingsService Fresh()
        {
            var settings = new SettingsService(new MainViewModel());
            Assert.False(settings.IsDirty);
            return settings;
        }

        [Fact]
        public void AMarkTheReloadMakesDoesNotCount()
        {
            var settings = Fresh();
            bool ran = false;
            settings.MirrorOnly(() =>
            {
                ran = true;
                settings.MarkDirty();
                settings.MarkDirty();
            });
            Assert.True(ran);
            Assert.False(settings.IsDirty);
        }

        [Fact]
        public void AMarkAfterTheReloadCounts()
        {
            var settings = Fresh();
            settings.MirrorOnly(() => settings.MarkDirty());
            Assert.False(settings.IsDirty);
            settings.MarkDirty();
            Assert.True(settings.IsDirty);
        }

        /// <summary>A change another thread marks while the reload runs is a
        /// real one: gyro calibration saves from a worker. The mark is made
        /// on a thread of its own. A task that is waited on can be run by
        /// the thread that waits: in a loop it was, 17,222 times of 20,000
        /// (measured 2026-11-11). This test used a task at first, and failed
        /// on its fifth run that way.</summary>
        [Fact]
        public void AMarkFromAnotherThreadDuringTheReloadCounts()
        {
            var settings = Fresh();
            settings.MirrorOnly(() =>
            {
                var worker = new Thread(() => settings.MarkDirty()) { IsBackground = true };
                worker.Start();
                Assert.True(worker.Join(10000), "the worker never finished");
            });
            Assert.True(settings.IsDirty);
        }

        [Fact]
        public void AReloadInsideAReloadKeepsTheOuterOneOpen()
        {
            var settings = Fresh();
            settings.MirrorOnly(() =>
            {
                settings.MirrorOnly(() => settings.MarkDirty());
                settings.MarkDirty();
            });
            Assert.False(settings.IsDirty);
            settings.MarkDirty();
            Assert.True(settings.IsDirty);
        }

        [Fact]
        public void AReloadThatThrowsStillEnds()
        {
            var settings = Fresh();
            Assert.Throws<InvalidOperationException>(
                () => settings.MirrorOnly(() => throw new InvalidOperationException()));
            settings.MarkDirty();
            Assert.True(settings.IsDirty);
        }

        [Fact]
        public void ANullReloadIsNothing()
        {
            var settings = Fresh();
            settings.MirrorOnly(null);
            settings.MarkDirty();
            Assert.True(settings.IsDirty);
        }

        /// <summary>The page open is the one reload that runs as a mirror. A
        /// second use needs its own proof that nothing in it wants saving.</summary>
        [Fact]
        public void OpeningAControllersPageReloadsItsRowsAsAMirror()
        {
            string code = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Matches(@"private void NavigateToTag\(string tag\)[\s\S]*?_settingsService\.MirrorOnly\(\(\) => InputService\.RefreshMappingsToViewModel\(padVm\)\);[\s\S]*?PadPageView\.DataContext = padVm;", code);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(code, @"\.MirrorOnly\("));
        }
    }
}
