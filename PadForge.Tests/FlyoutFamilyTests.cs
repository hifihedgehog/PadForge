using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Xml.Serialization;
using PadForge.Search;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The flyout family of #600 item 42: the profile flyout, the
    /// controllers on or off note, the device notes and the shift-layer
    /// flyout show on the monitor of the window in front, let clicks pass
    /// through, stay long enough to be read, and the controllers on or off
    /// note has its own row under Overlays, What Shows.
    /// </summary>
    [Collection("FlyoutPlacement")]
    public class FlyoutFamilyTests
    {
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;

        private static void Pump(Dispatcher dispatcher, int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background, (_, _) => frame.Continue = false, dispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }

        // ── Where a flyout stands ──

        [Fact]
        public void AFlyout_StandsAtTheBottomCenterOfTheWorkArea_InThatMonitorsPixels()
        {
            // A monitor right of the primary, at 150%: 1.5 pixels a DIP.
            var area = new FlyoutPlacement.WorkArea(2560, 0, 2560 + 3840, 2088, 1.5);
            var (x, y) = FlyoutPlacement.BottomCenter(area, 300, 70);
            Assert.Equal(2560 + (3840 - 450) / 2, x);
            Assert.Equal(2088 - 105, y);

            // A monitor left of the primary and above it, at 100%.
            var left = new FlyoutPlacement.WorkArea(-1920, -300, 0, 740, 1.0);
            Assert.Equal((-1920 + (1920 - 301) / 2, 740 - 71), FlyoutPlacement.BottomCenter(left, 300.4, 70.2));

            // The line takes the monitor's width less what stands beside it,
            // in DIPs, and never less than 160.
            Assert.Equal(3840 / 1.5 - FlyoutPlacement.Beside, FlyoutPlacement.LineWidth(area));
            Assert.Equal(FlyoutPlacement.LeastLine, FlyoutPlacement.LineWidth(new FlyoutPlacement.WorkArea(0, 0, 200, 200, 1.0)));
        }

        [Fact]
        public void TheMonitor_IsTheOneOfTheWindowInFront_ElseThePrimary()
        {
            var saved = FlyoutPlacement.ForegroundSeam;
            try
            {
                // No window in front: the primary monitor, which holds the
                // point 0,0 and whose work area WPF knows.
                FlyoutPlacement.ForegroundSeam = () => IntPtr.Zero;
                var primary = FlyoutPlacement.Front();
                Assert.True(primary.Width > 0 && primary.Bottom > primary.Top && primary.Scale >= 1.0);
                Assert.InRange(0, primary.Left, primary.Right - 1);

                // The real window in front: one of the monitors.
                FlyoutPlacement.ForegroundSeam = null;
                var front = FlyoutPlacement.Front();
                Assert.True(front.Width > 0 && front.Scale >= 1.0);
            }
            finally
            {
                FlyoutPlacement.ForegroundSeam = saved;
            }
        }

        /// <summary>Both windows of the family call the one placement, and
        /// none reads the primary monitor's work area any more.</summary>
        [Fact]
        public void EveryFlyout_UsesTheOnePlacement()
        {
            foreach (string file in new[] { "ProfileSwitchOverlay.xaml.cs", "ShiftLayerFlyout.xaml.cs" })
            {
                string source = ExtrasPageTests.RepoText("PadForge.App", "Views", file);
                Assert.Contains("FlyoutPlacement.Front()", source, StringComparison.Ordinal);
                Assert.Contains("FlyoutPlacement.Place(this, area);", source, StringComparison.Ordinal);
                Assert.Contains("FlyoutPlacement.Place(this, area)", source, StringComparison.Ordinal);
                Assert.DoesNotContain("SystemParameters.WorkArea", source, StringComparison.Ordinal);
                // Clicks pass through to the game.
                Assert.Contains("WS_EX_TRANSPARENT", source, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void TheProfileFlyout_LetsClicksThrough_TakesNoFocus_AndStandsOnTheFrontMonitor()
        {
            var saved = FlyoutPlacement.ForegroundSeam;
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                FlyoutPlacement.ForegroundSeam = () => IntPtr.Zero;
                var flyout = new ProfileSwitchOverlay();
                var layer = new ShiftLayerFlyout();
                try
                {
                    flyout.ShowProfileName("Racing");
                    Pump(dispatcher, 50);
                    var hwnd = new WindowInteropHelper(flyout).Handle;
                    int style = GetWindowLong(hwnd, GWL_EXSTYLE);
                    Assert.Equal(WS_EX_TRANSPARENT, style & WS_EX_TRANSPARENT);
                    Assert.Equal(WS_EX_NOACTIVATE, style & WS_EX_NOACTIVATE);
                    Assert.Equal(WS_EX_TOOLWINDOW, style & WS_EX_TOOLWINDOW);

                    // At the bottom center of the primary monitor's work area,
                    // the monitor of no window in front.
                    var area = FlyoutPlacement.Front();
                    Assert.True(GetWindowRect(hwnd, out var rect));
                    Assert.InRange(rect.Bottom, area.Bottom - 2, area.Bottom + 2);
                    Assert.InRange((rect.Left + rect.Right) / 2, area.Left + area.Width / 2 - 2, area.Left + area.Width / 2 + 2);
                    Assert.Equal(FlyoutPlacement.LineWidth(area), flyout.StatusText.MaxWidth);

                    // Any work area it is handed, a made-up one off every
                    // screen included: the window lands where Place says, in
                    // that area's pixels, not on the primary monitor.
                    var away = new FlyoutPlacement.WorkArea(-20000, -20000, -20000 + 2000, -20000 + 1000, 1.0);
                    FlyoutPlacement.Place(flyout, away);
                    Assert.True(GetWindowRect(hwnd, out var moved));
                    Assert.Equal(FlyoutPlacement.BottomCenter(away, flyout.ActualWidth, flyout.ActualHeight), (moved.Left, moved.Top));

                    layer.ShowLayer("Aim", "#FF6B2C", null);
                    Pump(dispatcher, 50);
                    var layerHwnd = new WindowInteropHelper(layer).Handle;
                    Assert.Equal(WS_EX_TRANSPARENT, GetWindowLong(layerHwnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT);
                    Assert.True(GetWindowRect(layerHwnd, out var layerRect));
                    Assert.InRange(layerRect.Bottom, area.Bottom - 2, area.Bottom + 2);
                    Assert.InRange((layerRect.Left + layerRect.Right) / 2, area.Left + area.Width / 2 - 2, area.Left + area.Width / 2 + 2);
                    Assert.Equal(System.Windows.TextTrimming.CharacterEllipsis, layer.LayerNameText.TextTrimming);
                }
                finally
                {
                    FlyoutPlacement.ForegroundSeam = saved;
                    flyout.StopTimers();
                    flyout.Close();
                    layer.Close();
                }
            }, TimeSpan.FromMinutes(2), "The flyout family test timed out.");
        }

        // ── How long a note stays ──

        [Fact]
        public void ADrop_StaysFourSeconds_AndEveryOtherNoteTwo()
        {
            Assert.Equal(TimeSpan.FromSeconds(4), ProfileSwitchOverlay.DeviceNoteTime(connected: false));
            Assert.Equal(TimeSpan.FromSeconds(2), ProfileSwitchOverlay.DeviceNoteTime(connected: true));
            Assert.Equal(TimeSpan.FromSeconds(2), ProfileSwitchOverlay.NoteTime);

            // A drop, a burst of drops and a device that keeps disconnecting
            // are all shown as drops.
            string notes = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.DeviceNotes.cs");
            Assert.Contains("SwitchOverlay().ShowDeviceNote(text, note.Kind == DeviceNoteKind.Connected);", notes, StringComparison.Ordinal);
            string overlay = ExtrasPageTests.RepoText("PadForge.App", "Views", "ProfileSwitchOverlay.xaml.cs");
            Assert.Contains("_dismissTimer.Interval = DeviceNoteTime(connected);", overlay, StringComparison.Ordinal);
        }

        // ── The controllers on or off note's row ──

        [Fact]
        public void TheControllersNote_IsOnByDefault_InTheAppTheSettingsFileAndAProfile()
        {
            var vm = new DashboardViewModel();
            Assert.True(vm.EnableControllersNote);
            Assert.True(new AppSettingsData().EnableControllersNote);
            Assert.True(new ProfileData().EnableControllersNote);

            vm.EnableControllersNote = false;
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            Assert.True(DashboardViewModel.CanResetSetting(nameof(DashboardViewModel.EnableControllersNote)));
            vm.ResetSettingCommand.Execute(nameof(DashboardViewModel.EnableControllersNote));
            Assert.True(vm.EnableControllersNote);
            Assert.Contains(nameof(DashboardViewModel.EnableControllersNote), raised);
            Assert.Contains(nameof(DashboardViewModel.OverlaysAnyEnabled), raised);
        }

        /// <summary>The switch round-trips for the app and a profile, and a
        /// file or a profile from before it, with Overlays on as these are,
        /// reads on, so the note shows as it always did. One with Overlays
        /// off reads off (ControllersNoteOldFileTests).</summary>
        [Fact]
        public void AFileFromBeforeTheRow_ReadsTheNoteOn()
        {
            var app = new XmlSerializer(typeof(AppSettingsData));
            foreach (bool on in new[] { false, true })
            {
                var written = new StringWriter();
                app.Serialize(written, new AppSettingsData { EnableControllersNote = on });
                Assert.Equal(on, ((AppSettingsData)app.Deserialize(new StringReader(written.ToString()))).EnableControllersNote);
            }
            var oldApp = new StringWriter();
            app.Serialize(oldApp, new AppSettingsData { EnableControllersNote = false });
            string before = Regex.Replace(oldApp.ToString(), @"\s*<EnableControllersNote>false</EnableControllersNote>", "");
            Assert.DoesNotContain("EnableControllersNote", before, StringComparison.Ordinal);
            Assert.True(((AppSettingsData)app.Deserialize(new StringReader(before))).EnableControllersNote);

            var profile = new XmlSerializer(typeof(ProfileData));
            var written2 = new StringWriter();
            profile.Serialize(written2, new ProfileData { Id = "p", Name = "Game", EnableControllersNote = false });
            Assert.False(((ProfileData)profile.Deserialize(new StringReader(written2.ToString()))).EnableControllersNote);
            string old = Regex.Replace(written2.ToString(), @"\s*<EnableControllersNote>false</EnableControllersNote>", "");
            Assert.DoesNotContain("EnableControllersNote", old, StringComparison.Ordinal);
            Assert.True(((ProfileData)profile.Deserialize(new StringReader(old))).EnableControllersNote);
        }

        /// <summary>The switch gates the note and nothing else: the
        /// controllers turn on or off either way. The profile's copy follows
        /// the app's at every place its siblings do.</summary>
        [Fact]
        public void TheRow_GatesTheNote_AndTheProfileKeepsItsOwnCopy()
        {
            string input = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs");
            int toggled = input.IndexOf("ToggleVCsDisabled?.Invoke();", StringComparison.Ordinal);
            int gate = input.IndexOf("if (_mainVm.Dashboard.EnableControllersNote)", toggled, StringComparison.Ordinal);
            int shown = input.IndexOf("ShowVCsToggleOverlay(anyEnabled);", toggled, StringComparison.Ordinal);
            Assert.True(toggled > 0 && gate > toggled && shown > gate);
            foreach (string place in new[]
            {
                "EnableControllersNote = _mainVm.Dashboard.EnableControllersNote,",
                "_mainVm.Dashboard.EnableControllersNote = profile.EnableControllersNote;",
                "profile.EnableControllersNote = snapshot.EnableControllersNote;",
            })
                Assert.Contains(place, input, StringComparison.Ordinal);
            string settings = ExtrasPageTests.RepoText("PadForge.App", "Services", "SettingsService.cs");
            foreach (string place in new[]
            {
                "_mainVm.Dashboard.EnableControllersNote = appSettings.EnableControllersNote;",
                "_mainVm.Dashboard.EnableControllersNote = active.EnableControllersNote;",
                "profile.EnableControllersNote = _mainVm.Dashboard.EnableControllersNote;",
                "EnableControllersNote = _mainVm.Dashboard.EnableControllersNote,",
            })
                Assert.Contains(place, settings, StringComparison.Ordinal);
        }

        [Fact]
        public void Search_FindsTheRow_AsItFindsItsSiblings()
        {
            var markup = SearchSources.FileNames.ToDictionary(p => p.Key, p => ExtrasPageTests.RepoText("PadForge.App", "Views", p.Value));
            var entries = SearchIndexBuilder.Read(markup, PadPage.TabOfGroupName).Entries;
            var sibling = Assert.Single(entries, e => e.Kind == SearchKind.Setting && e.Label.Key == "Dashboard_ProfileOverlay" && e.ExtraKey == "Overlays");
            var entry = Assert.Single(entries, e => e.Kind == SearchKind.Setting && e.Label.Key == "Dashboard_ControllersNote");
            Assert.Equal(SearchPage.Extras, entry.Page);
            Assert.Equal("Overlays", entry.ExtraKey);
            Assert.Equal("EnableControllersNote", entry.Value?.Path);
            Assert.Equal(SearchValueKind.Switch, entry.Value?.Kind);
            Assert.Equal(sibling.CardTitle, entry.CardTitle);
            Assert.True(entry.Order > sibling.Order);
        }
    }

    [CollectionDefinition("FlyoutPlacement", DisableParallelization = true)]
    public sealed class FlyoutPlacementCollection
    {
    }
}
