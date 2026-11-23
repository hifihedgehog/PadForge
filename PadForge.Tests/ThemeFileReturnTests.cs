using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Common.Themes;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Wpf.Ui.Appearance;
using Xunit;
using InputManager = PadForge.Common.Input.InputManager;

namespace PadForge.Tests
{
    /// <summary>A picked theme file whose folder was gone when the look went
    /// on, and that comes back. These share the one WPF application of a
    /// test run, so they run alone, which also lets the settings load write
    /// the settings statics it writes.</summary>
    [Collection("WpfApplicationSingleton")]
    public class ThemeFileReturnTests
    {
        private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

        private static void Pump(Dispatcher dispatcher) => dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        private static void Until(Dispatcher dispatcher, Func<bool> done, string what)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                Pump(dispatcher);
                if (done()) return;
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), what + " did not happen");
                Thread.Sleep(10);
            }
        }

        private static string TempFolder()
        {
            string folder = Path.Combine(Path.GetTempPath(), "PadForgeThemeReturn-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static void Drop(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static ThemeDefinition Back(string accent, string name = "Back")
        {
            Assert.True(ThemeColor.TryParse(accent, out Color color));
            Assert.True(ThemeColor.TryParse("#101418", out Color ground));
            return new ThemeDefinition { Kind = ThemeKind.File, Name = name, Dark = true, Background = ground, Accent = color };
        }

        private static void BackToStock()
        {
            ThemeManager.SetPreview(null);
            ThemeManager.SetGameLook(null);
            ThemeManager.LoadRegistry(null);
            ThemeManager.Apply(2, null);
            PageRenderDump.ApplyTheme(ApplicationTheme.Dark);
            EmberTheme.ApplyAccent();
            WebThemeSheet.Refresh();
        }

        /// <summary>The settings statics a settings load writes, put back after.</summary>
        private sealed class SavedStatics : IDisposable
        {
            private readonly DeviceCollection _devices = SettingsManager.UserDevices;
            private readonly SettingsCollection _settings = SettingsManager.UserSettings;
            private readonly List<ProfileData> _profiles = SettingsManager.Profiles;
            private readonly string _active = SettingsManager.ActiveProfileId;
            private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
            private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
            private readonly bool[] _enabled = (bool[])SettingsManager.SlotEnabled.Clone();
            private readonly List<int> _xbox = SettingsManager.XboxSlotOrder;
            private readonly List<int> _playStation = SettingsManager.PlayStationSlotOrder;
            private readonly List<int> _extended = SettingsManager.ExtendedSlotOrder;
            private readonly List<int> _kbm = SettingsManager.KeyboardMouseSlotOrder;
            private readonly List<int> _midi = SettingsManager.MidiSlotOrder;

            public SavedStatics()
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.Profiles = new List<ProfileData>();
                SettingsManager.ActiveProfileId = null;
                SettingsManager.PendingDefaultSnapshot = null;
                Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
                Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);
                SettingsManager.SlotCreated[0] = true;
                SettingsManager.SlotEnabled[0] = true;
                SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
                SettingsManager.XboxSlotOrder = new List<int> { 0 };
                SettingsManager.PlayStationSlotOrder = new List<int>();
                SettingsManager.ExtendedSlotOrder = new List<int>();
                SettingsManager.KeyboardMouseSlotOrder = new List<int>();
                SettingsManager.MidiSlotOrder = new List<int>();
            }

            public void Dispose()
            {
                SettingsManager.UserDevices = _devices;
                SettingsManager.UserSettings = _settings;
                SettingsManager.Profiles = _profiles;
                SettingsManager.ActiveProfileId = _active;
                SettingsManager.PendingDefaultSnapshot = null;
                SettingsManager.SlotMappingSets = _sets;
                Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
                Array.Copy(_enabled, SettingsManager.SlotEnabled, _enabled.Length);
                SettingsManager.XboxSlotOrder = _xbox;
                SettingsManager.PlayStationSlotOrder = _playStation;
                SettingsManager.ExtendedSlotOrder = _extended;
                SettingsManager.KeyboardMouseSlotOrder = _kbm;
                SettingsManager.MidiSlotOrder = _midi;
            }
        }

        private static void Invoke(object target, string method, params object[] args)
        {
            try
            {
                target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
            }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); }
        }

        /// <summary>Reload in Settings reads the theme files again, and the
        /// same pick put nothing on, so PadForge stayed on Dark under a pick
        /// that reads well again.</summary>
        [Fact]
        public void ASettingsReload_PutsOnAPickedFileThatIsBack()
        {
            string folder = TempFolder();
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                using var statics = new SavedStatics();
                try
                {
                    string path = Path.Combine(folder, "drive", "back.pftheme");
                    var vm = new MainViewModel();
                    var service = new SettingsService(vm);
                    var loaded = (AppSettingsData)typeof(SettingsService)
                        .GetMethod("BuildAppSettings", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(service, null);
                    loaded.ThemeFiles = new[] { new ThemeFileData { Name = "Back", Path = path } };
                    loaded.ThemeIndex = 2;
                    loaded.ThemeId = ThemeManager.FilePrefix + path;

                    // The look goes on while the folder is absent: Dark, the pick Missing.
                    Invoke(service, "LoadAppSettings", loaded);
                    ThemeManager.Apply(vm.Settings.SelectedThemeIndex, vm.Settings.SelectedThemeId);
                    Assert.Equal(ThemeFault.Missing, ThemeManager.Find(loaded.ThemeId).Fault);
                    Assert.Null(ThemeManager.Current);

                    // The folder is back, and Reload loads the same settings.
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    ThemeFile.Write(path, Back("#22CC88"));
                    Invoke(service, "LoadAppSettings", loaded);
                    Assert.Equal("Back", ThemeManager.Current?.Name);
                }
                finally
                {
                    BackToStock();
                    Drop(folder);
                }
            }, Limit, "the settings reload test timed out");
        }

        /// <summary>The watch on a picked file whose folder was absent was
        /// recorded with no watcher behind it, and every later look returned
        /// on the same path, so the file was never watched again.</summary>
        [Fact]
        public void APickedFileWhoseFolderWasAbsent_IsWatchedOnceTheFolderIsBack()
        {
            string folder = TempFolder();
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    string path = Path.Combine(folder, "drive", "watched.pftheme");
                    string id = ThemeManager.FilePrefix + path;
                    ThemeManager.LoadRegistry(new[] { ("Back", path) });
                    ThemeManager.Apply(2, id);
                    Assert.Null(ThemeManager.Current);

                    // The folder comes back. The next look watches the file.
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    ThemeManager.Apply(2, id);

                    // Written as an editor or a restore writes it: the look follows by itself.
                    ThemeFile.Write(path, Back("#CC2288"));
                    Until(dispatcher, () => ThemeManager.Current?.Name == "Back", "the returned file's write");
                }
                finally
                {
                    BackToStock();
                    Drop(folder);
                }
            }, Limit, "the watched folder test timed out");
        }

        /// <summary>A watcher whose read fails (the drive that holds the file
        /// is taken out) stops and raises Error. It stayed in place under the
        /// same path, so every later look kept the dead watcher and no edit of
        /// the file was read again.</summary>
        [Fact]
        public void APickedFileWhoseWatcherFailed_IsReadAgainAndWatchedAnew()
        {
            string folder = TempFolder();
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    string path = Path.Combine(folder, "drive", "watched.pftheme");
                    string id = ThemeManager.FilePrefix + path;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    ThemeFile.Write(path, Back("#22CC88"));
                    ThemeManager.LoadRegistry(new[] { ("Back", path) });
                    ThemeManager.Apply(2, id);
                    Assert.Equal("Back", ThemeManager.Current?.Name);

                    // The watcher fails as FileSystemWatcher does on a failed
                    // directory read: raising stops, then Error is raised.
                    var watcher = (FileSystemWatcher)typeof(ThemeManager)
                        .GetField("_watcher", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                    Assert.NotNull(watcher);
                    watcher.EnableRaisingEvents = false;
                    Invoke(watcher, "OnError", new ErrorEventArgs(new System.ComponentModel.Win32Exception(21)));

                    // The file is read again, and a watcher stands behind it once more.
                    ThemeFile.Write(path, Back("#CC2288", "Edited"));
                    Until(dispatcher, () => ThemeManager.Current?.Name == "Edited", "the read after the failed watcher");
                    ThemeFile.Write(path, Back("#2288CC", "Again"));
                    Until(dispatcher, () => ThemeManager.Current?.Name == "Again", "the next write, seen by a new watcher");
                }
                finally
                {
                    BackToStock();
                    Drop(folder);
                }
            }, Limit, "the failed watcher test timed out");
        }
    }
}
