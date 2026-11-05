using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Key Colors card, the macro key pickers, the Map Key Lights
    /// dialog, the menu option lists and the settings dirty gate, in the
    /// shapes a click, a drag, a language switch or a save reaches them.
    /// </summary>
    public class AuditDelta20261104GuiTests
    {
        private static readonly LightMap Keys = new(new[]
        {
            new LightInfo(0, LightKind.Key, 0x29, LightKeySource.Device),   // Esc
            new LightInfo(1, LightKind.Key, 0x04, LightKeySource.Device),   // A
            new LightInfo(2, LightKind.Key, 0x1A, LightKeySource.Device),   // W
            new LightInfo(3, LightKind.Key, 0x16, LightKeySource.Device),   // S
        }, "test");

        private static readonly LightMap EdgeOnly = new(new[]
        {
            new LightInfo(0, LightKind.Edge, 0, LightKeySource.None),
            new LightInfo(1, LightKind.Edge, 0, LightKeySource.None),
        }, "test");

        private static ShiftLayerInfo Base() => new() { LayerMask = "Base", LayerName = "Base" };

        private static string RepoText(string relative)
            => File.ReadAllText(RepoPath(relative.Split('/'))).Replace("\r\n", "\n");

        private static string RepoPath(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        }

        private static void RunSta(Action body)
        {
            Exception failure = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            Assert.True(t.Join(60000), "STA test body timed out");
            if (failure != null) throw failure;
        }

        // ─────────────────────────────────────────────
        //  Drag selection adds to the selection
        // ─────────────────────────────────────────────

        /// <summary>The picture hands SetSelection the selection followed by
        /// the lights a drag covered, as a lazy sequence over the live
        /// selection. The call clears that selection before it reads the
        /// sequence, so the existing picks have to survive the clear.</summary>
        [Fact]
        public void KeyColorsCard_DragSelection_KeepsWhatWasAlreadySelected()
        {
            var card = new KeyColorsCardViewModel();
            card.Attach(new DeviceSlotConfig(), Keys, new[] { Base() });
            card.SetSelection(new[] { 1, 2 });

            card.SetSelection(card.Selection.Concat(new[] { 3 }.Select(i => i)));

            Assert.Equal(new[] { 1, 2, 3 }, card.Selection.OrderBy(i => i).ToArray());
        }

        [Fact]
        public void KeyPicker_DragSelection_KeepsWhatWasAlreadySelectedInItsOrder()
        {
            var action = new MacroAction { Type = MacroActionType.SetKeyColors };
            var picker = action.KeyTargetPicker;
            IKeyLightsSource source = picker;
            int a = picker.Map.IndexesOf(0x04)[0], w = picker.Map.IndexesOf(0x1A)[0], s = picker.Map.IndexesOf(0x16)[0];
            source.Toggle(w);
            source.Toggle(a);

            source.SetSelection(source.Selection.Concat(new[] { s }.Select(i => i)));

            Assert.Equal("k1A,k04,k16", action.KeyColorTargets);
        }

        // ─────────────────────────────────────────────
        //  Select by Pressing Keys
        // ─────────────────────────────────────────────

        [Fact]
        public void KeyColorsCard_PickByPressing_EndsWhenTheCardMovesToADeviceWithoutKeys()
        {
            var card = new KeyColorsCardViewModel();
            var config = new DeviceSlotConfig();
            card.Attach(config, Keys, new[] { Base() });
            card.PickByPressing = true;

            card.Attach(config, EdgeOnly, new[] { Base() });

            Assert.False(card.HasKeys);
            Assert.False(card.PickByPressing);
        }

        [Fact]
        public void KeyColorsCard_PickByPressing_StaysOnWhenTheCardMovesToAnotherKeyboard()
        {
            var card = new KeyColorsCardViewModel();
            card.Attach(new DeviceSlotConfig(), Keys, new[] { Base() });
            card.PickByPressing = true;

            card.Attach(new DeviceSlotConfig(), Keys, new[] { Base() });

            Assert.True(card.PickByPressing);
        }

        // ─────────────────────────────────────────────
        //  Language switch
        // ─────────────────────────────────────────────

        [Fact]
        public void KeyColorsCard_UpdateLayers_RaisesTheTextsThatReadTheLanguage()
        {
            var card = new KeyColorsCardViewModel();
            card.Attach(new DeviceSlotConfig(), Keys, new[] { Base() });
            var raised = new List<string>();
            card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            card.UpdateLayers(new[] { Base() });

            Assert.Contains(nameof(KeyColorsCardViewModel.Title), raised);
            Assert.Contains(nameof(KeyColorsCardViewModel.Description), raised);
            Assert.Contains(nameof(KeyColorsCardViewModel.SelectionSummary), raised);
        }

        // ─────────────────────────────────────────────
        //  The picture
        // ─────────────────────────────────────────────

        private static Canvas CanvasOf(KeyLightsView view)
            => (Canvas)((Viewbox)((Border)view.Content).Child).Child;

        /// <summary>A press in the picture does not bubble on to an ancestor
        /// ScrollViewer, which takes the keyboard focus on a left press and
        /// would take it from the light the picture just focused, ending
        /// Select by Pressing Keys.</summary>
        [Fact]
        public void KeyPicture_ThePressStopsInThePicture_SoAnAncestorScrollViewerKeepsHandsOffTheFocus() => RunSta(() =>
        {
            var view = new KeyLightsView();
            var scroller = new ScrollViewer { Content = view };
            var canvas = CanvasOf(view);
            bool reached = false;
            scroller.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) => reached = true));
            var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                Source = canvas,
            };

            canvas.RaiseEvent(press);

            Assert.True(press.Handled);
            Assert.False(reached);
        });

        private static void Pump(Window window)
            => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        /// <summary>A picture that is out of sight (a collapsed macro action
        /// card) draws nothing until it is shown, then draws what its source
        /// holds.</summary>
        [Fact]
        public void KeyPicture_DrawsNothingWhileCollapsed_AndDrawsWhenShown() => RunSta(() =>
        {
            var card = new KeyColorsCardViewModel();
            card.Attach(new DeviceSlotConfig(), Keys, new[] { Base() });
            var view = new KeyLightsView { Visibility = Visibility.Collapsed };
            view.Resources["TelemetryFontFamily"] = new System.Windows.Media.FontFamily("Segoe UI");
            view.Source = card;
            var window = new Window
            {
                Content = view, Width = 800, Height = 400, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, WindowStyle = WindowStyle.None,
            };
            try
            {
                window.Show();
                Pump(window);
                var canvas = CanvasOf(view);
                // Nothing is drawn while the picture is out of sight.
                Assert.Empty(canvas.Children);

                view.Visibility = Visibility.Visible;
                Pump(window);

                Assert.Equal(1 + Keys.Lights.Count, canvas.Children.Count);
            }
            finally { window.Close(); }
        });

        // ─────────────────────────────────────────────
        //  Menu option lists
        // ─────────────────────────────────────────────

        private sealed class OptionsHost : INotifyPropertyChanged
        {
            private MenuIntOption[] _options;
            public MenuIntOption[] Options
            {
                get => _options;
                set { _options = value; Raise(nameof(Options)); }
            }
            public int Index { get; set; } = 1;
            public void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            public event PropertyChangedEventHandler PropertyChanged;
        }

        /// <summary>The option lists are rebuilt in the new language on a
        /// switch and the value is raised again. The box shows the new
        /// label only when the new item is not equal to the one it holds.</summary>
        [Fact]
        public void MenuIntOption_AfterALanguageSwitch_TheClosedBoxShowsTheNewLabel() => RunSta(() =>
        {
            var host = new OptionsHost
            {
                Options = new[]
                {
                    new MenuIntOption { Value = 0, Label = "Radial" },
                    new MenuIntOption { Value = 1, Label = "Grid" },
                },
            };
            var combo = new ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Value", DataContext = host };
            combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(OptionsHost.Options)));
            combo.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty,
                new Binding(nameof(OptionsHost.Index)) { Mode = BindingMode.TwoWay });
            var window = new Window
            {
                Content = combo, Width = 300, Height = 120, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, WindowStyle = WindowStyle.None,
            };
            try
            {
                window.Show();
                Pump(window);
                Assert.Equal("Grid", Assert.IsType<MenuIntOption>(combo.SelectedItem).Label);

                host.Options = new[]
                {
                    new MenuIntOption { Value = 0, Label = "Radial-DE" },
                    new MenuIntOption { Value = 1, Label = "Raster-DE" },
                };
                host.Raise(nameof(OptionsHost.Index));
                Pump(window);

                Assert.Equal("Raster-DE", Assert.IsType<MenuIntOption>(combo.SelectedItem).Label);
                Assert.Equal(1, host.Index);
            }
            finally { window.Close(); }
        });

        [Fact]
        public void MenuIntOption_And_MenuHostOption_AreEqualOnlyWhenTheyShowTheSameText()
        {
            Assert.Equal(new MenuIntOption { Value = 1, Label = "Grid", Description = "a" },
                new MenuIntOption { Value = 1, Label = "Grid", Description = "a" });
            Assert.NotEqual(new MenuIntOption { Value = 1, Label = "Grid" }, new MenuIntOption { Value = 1, Label = "Raster" });
            Assert.NotEqual(new MenuIntOption { Value = 1, Label = "Grid", Description = "a" },
                new MenuIntOption { Value = 1, Label = "Grid", Description = "b" });
            Assert.Equal(new MenuHostOption { Descriptor = "Custom", Label = "Custom" },
                new MenuHostOption { Descriptor = "Custom", Label = "Custom" });
            Assert.NotEqual(new MenuHostOption { Descriptor = "Custom", Label = "Custom" },
                new MenuHostOption { Descriptor = "Custom", Label = "Eigene" });
        }

        // ─────────────────────────────────────────────
        //  Settings dirty gate
        // ─────────────────────────────────────────────

        [Fact]
        public void EveryBatteryNoticeSetting_MarksTheSettingsFileDirty()
        {
            string mainWindow = RepoText("PadForge.App/MainWindow.xaml.cs");
            var names = typeof(SettingsViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name.StartsWith("BatteryNotify", StringComparison.Ordinal) && p.CanWrite)
                .Select(p => p.Name).ToList();
            Assert.True(names.Count >= 5, "the battery notice settings moved");
            foreach (string name in names)
                Assert.Contains("nameof(SettingsViewModel." + name + ")", mainWindow);
        }

        // ─────────────────────────────────────────────
        //  Copy / Paste stand-in
        // ─────────────────────────────────────────────

        [Fact]
        public void TheStandInChoice_CountsKeyAndGroupColors_ButNotASingleLightsColor()
        {
            var data = new DeviceSlotConfigData { KeyLighting = "Base>k04=FF0000" };
            Assert.True(SettingsService.IsDeviceSlotConfigDataConfigured(data));
            var config = new DeviceSlotConfig { KeyLighting = "Base>gedge=00FF00" };
            Assert.True(SettingsService.IsDeviceConfigConfigured(config));

            // A single light is set by the source device's own light number,
            // and a stand-in carries none of those.
            Assert.False(SettingsService.IsDeviceSlotConfigDataConfigured(new DeviceSlotConfigData { KeyLighting = "Base>l3=FF0000" }));
            Assert.False(SettingsService.IsDeviceConfigConfigured(new DeviceSlotConfig { KeyLighting = "Base>l3=FF0000" }));
            Assert.False(SettingsService.IsDeviceSlotConfigDataConfigured(new DeviceSlotConfigData()));
        }

        // ─────────────────────────────────────────────
        //  XAML
        // ─────────────────────────────────────────────

        private static string ElementAround(string xaml, string anchor)
        {
            int at = xaml.IndexOf(anchor, StringComparison.Ordinal);
            Assert.True(at > 0, "not found: " + anchor);
            int start = xaml.LastIndexOf('<', at);
            int end = xaml.IndexOf('>', at);
            return xaml.Substring(start, end - start + 1);
        }

        [Fact]
        public void TheKeyDurationSlider_DoesNotSpanTheWholeRangeOnItsShortTrack()
        {
            string xaml = RepoText("PadForge.App/Views/PadPage.xaml");
            string slider = ElementAround(xaml, "<Slider Value=\"{Binding KeyColorMs");
            Assert.DoesNotContain("Maximum=\"600000\"", slider);
            Assert.Contains("LogScaleConverter", slider);
        }

        [Fact]
        public void TheLogScaleConverter_PutsADurationOnTheSliderByRatio()
        {
            var converter = new PadForge.Converters.LogScaleConverter();
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            object max = "600000";

            Assert.Equal(0d, (double)converter.Convert(1, typeof(double), max, culture), 6);
            Assert.Equal(100d, (double)converter.Convert(600000, typeof(double), max, culture), 6);
            // Out of range numbers stay on the track.
            Assert.Equal(0d, (double)converter.Convert(0, typeof(double), max, culture), 6);
            Assert.Equal(100d, (double)converter.Convert(900000, typeof(double), max, culture), 6);

            Assert.Equal(1, converter.ConvertBack(0d, typeof(int), max, culture));
            Assert.Equal(600000, converter.ConvertBack(100d, typeof(int), max, culture));
            Assert.Equal(1, converter.ConvertBack(-5d, typeof(int), max, culture));
            Assert.Equal(600000, converter.ConvertBack(250d, typeof(int), max, culture));

            // Each step of 1 changes the duration by the same ratio (about 14%),
            // and the default of 3000 ms sits near 60 on the track, reachable.
            double at3000 = (double)converter.Convert(3000, typeof(double), max, culture);
            Assert.InRange(at3000, 59, 62);
            Assert.Equal(3000, converter.ConvertBack(at3000, typeof(int), max, culture));
            int step = (int)converter.ConvertBack(at3000 + 1, typeof(int), max, culture);
            Assert.InRange(step / 3000d, 1.12, 1.16);
        }

        [Fact]
        public void TheMapKeyLightsStatusAndResultLines_AreLiveRegions()
        {
            string xaml = RepoText("PadForge.App/Views/MapKeyLightsDialog.xaml");
            Assert.Contains("LiveText.Polite=\"True\"", ElementAround(xaml, "Text=\"{Binding Status}\""));
            Assert.Contains("LiveText.Polite=\"True\"", ElementAround(xaml, "x:Name=\"NoteText\""));
        }
    }

    /// <summary>
    /// The settings file write. A save builds the file beside the settings
    /// file and swaps it in, so a process killed mid-save leaves the old
    /// file whole.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class SettingsFileWriteTests : IDisposable
    {
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly List<ProfileData> _profiles = SettingsManager.Profiles;
        private readonly string _active = SettingsManager.ActiveProfileId;
        private readonly ProfileData _pendingDefault = SettingsManager.PendingDefaultSnapshot;
        private readonly bool[] _created = SettingsManager.SlotCreated;
        private readonly bool[] _enabled = SettingsManager.SlotEnabled;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly bool _stale = InputService.VmMappingsStale;
        private readonly bool _reloading = InputService.SuppressMappingEditPush;

        public void Dispose()
        {
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.Profiles = _profiles;
            SettingsManager.ActiveProfileId = _active;
            SettingsManager.PendingDefaultSnapshot = _pendingDefault;
            SettingsManager.SlotCreated = _created;
            SettingsManager.SlotEnabled = _enabled;
            SettingsManager.SlotMappingSets = _sets;
            InputService.VmMappingsStale = _stale;
            InputService.SuppressMappingEditPush = _reloading;
        }

        private static SettingsService Arrange()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.Profiles = new List<ProfileData>();
            SettingsManager.ActiveProfileId = null;
            SettingsManager.PendingDefaultSnapshot = null;
            SettingsManager.SlotCreated = new bool[PadForge.Common.Input.InputManager.MaxPads];
            SettingsManager.SlotEnabled = new bool[PadForge.Common.Input.InputManager.MaxPads];
            SettingsManager.SlotMappingSets = new MappingSet[PadForge.Common.Input.InputManager.MaxPads];
            InputService.VmMappingsStale = false;
            InputService.SuppressMappingEditPush = false;
            var vm = new MainViewModel();
            return new SettingsService(vm);
        }

        private static string NewPath() => Path.Combine(Path.GetTempPath(), "padforge-write-" + Guid.NewGuid().ToString("N"));

        private static void Remove(string path)
        {
            foreach (string file in new[] { path, path + ".tmp" })
                if (File.Exists(file)) File.Delete(file);
        }

        [Fact]
        public void ASave_SwapsInANewFile_AndNeverWritesIntoTheOldOne()
        {
            var settings = Arrange();
            string path = NewPath();
            try
            {
                File.WriteAllText(path, "old settings");
                // A reader that lets the file be replaced sees the file it
                // opened. A save that truncates and rewrites that same file
                // changes what the reader sees.
                using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    Assert.True(settings.SaveToFile(path));
                    held.Position = 0;
                    Assert.Equal("old settings", new StreamReader(held).ReadToEnd());
                }
                Assert.StartsWith("<?xml", File.ReadAllText(path));
                Assert.False(File.Exists(path + ".tmp"));
            }
            finally { Remove(path); }
        }

        [Fact]
        public void ASave_ReplacesTheLeftoverOfAKilledSave_AndLeavesNoFileBehind()
        {
            var settings = Arrange();
            string path = NewPath();
            try
            {
                File.WriteAllText(path + ".tmp", "half a file");
                Assert.True(settings.SaveToFile(path));
                Assert.StartsWith("<?xml", File.ReadAllText(path));
                Assert.False(File.Exists(path + ".tmp"));
            }
            finally { Remove(path); }
        }

        /// <summary>A folder where the temporary file cannot be made still
        /// gets its settings saved, straight into the file, as a save always
        /// did.</summary>
        [Fact]
        public void ASaveThatCannotMakeItsTemporaryFile_StillWritesTheSettingsFile()
        {
            var settings = Arrange();
            string path = NewPath();
            Directory.CreateDirectory(path + ".tmp");
            try
            {
                File.WriteAllText(path, "the old file");
                Assert.True(settings.SaveToFile(path));
                Assert.StartsWith("<?xml", File.ReadAllText(path));
            }
            finally
            {
                Directory.Delete(path + ".tmp");
                Remove(path);
            }
        }
    }
}
