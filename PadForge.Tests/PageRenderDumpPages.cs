using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PadForge.Engine;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Wpf.Ui.Appearance;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Draws the Profiles, Settings and About pages and the Get
    /// Profiles window to pictures without a screen (#574), as
    /// PageRenderDump draws the controller's page: under the app's own
    /// resources and theme, at the page's size in its board's frame, in
    /// states a real setup may not have.
    /// Each test does nothing unless PADFORGE_PAGE_RENDER_DIR names a folder,
    /// and then writes one PNG per state into it, at the scale in
    /// PADFORGE_PAGE_RENDER_SCALE (1.5 when unset), the steps it reached into
    /// steps.txt, and lookups.txt: for each state, every binding of a part
    /// that shows which looks up an ancestor or a named element and found
    /// none.</summary>
    [Collection("WpfApplicationSingleton")]
    public class PageRenderDumpPages
    {
        // A page in a board's frame: the frame's width less the open pane and
        // its edge, and its height less the title bar.
        private const double PaneAndEdge = 249, TitleBar = 48;

        /// <summary>The Profiles board's page (the world-class pass): Default
        /// active beside Forza Wheel and the drop tile, the four starter
        /// profiles, switching on with the game in front, and one shortcut,
        /// Next Profile, on two recorded buttons. Its widths are a page's
        /// beside the sidebar of 264: the board's 1440 frame, then 1280,
        /// the window's own 1100 and its least, 900.</summary>
        [Fact]
        public void DrawProfiles()
        {
            Run("profiles", drawing =>
            {
                const double Board = 1440 - 264, Wide = 1280 - 264, Usual = 1100 - 264, Least = 900 - 264;
                double height = 900 - 52;
                var vm = new SettingsViewModel();
                ProfileListItem Profile(string id, string name, string game, params VirtualControllerType[] slots)
                {
                    var item = new ProfileListItem
                    {
                        Id = id, Name = name,
                        Executables = game == null ? null : Path.GetFileName(game),
                        ExecutablePaths = game,
                    };
                    item.SetTopology(slots.Select(_ => true).ToArray(), slots.Select(t => (int)t).ToArray());
                    return item;
                }
                var standard = Profile(ProfileListItem.DefaultProfileId, "Default", null,
                    VirtualControllerType.Xbox, VirtualControllerType.PlayStation, VirtualControllerType.Nintendo);
                var rocket = Profile("forza-wheel", "Forza Wheel", @"C:\Games\ForzaHorizon5\ForzaHorizon5.exe",
                    VirtualControllerType.Xbox, VirtualControllerType.Extended);
                var desktop = Profile("desktop", "Desktop", null, VirtualControllerType.KeyboardMouse);
                var wasd = Profile("wasd", "WASD and Mouse", null, VirtualControllerType.KeyboardMouse);
                foreach (var item in new[] { standard, rocket }) vm.ProfileItems.Add(item);
                // Which profile is active is the app's to say, from a static
                // the dump leaves alone, so the board's is set here.
                foreach (var item in vm.ProfileItems) item.IsActive = item == standard;
                vm.EnableAutoProfileSwitching = true;
                vm.ForegroundExeName = "ForzaHorizon5.exe";
                vm.ForegroundProfileName = "Forza Wheel";
                vm.IsForegroundMatched = true;
                var next = new ProfileShortcutViewModel(new GlobalMacroData
                {
                    SwitchMode = SwitchProfileMode.Next,
                    TriggerEntries = new[] { new TriggerButtonEntry { ButtonIndex = 10 }, new TriggerButtonEntry { ButtonIndex = 5 } },
                }, null, null);
                vm.ProfileShortcuts.Add(next);

                var page = new ProfilesPage { DataContext = vm };
                var ground = drawing.Ground(page);
                drawing.Step("the page is made");

                // The board's frame, then the narrower windows, each tall
                // enough for the whole page.
                drawing.Picture(ground, "profiles.png", Board, height);
                drawing.Picture(ground, "profiles-1280.png", Wide, 1000);
                drawing.Picture(ground, "profiles-1100.png", Usual, 1250);
                drawing.Picture(ground, "profiles-900.png", Least, 1500);
                double width = Board;

                // Four profiles: the cards and the drop tile start a second line.
                drawing.Picture(ground, "profiles-four.png", width, 1050, () =>
                {
                    vm.ProfileItems.Add(desktop);
                    vm.ProfileItems.Add(wasd);
                });
                vm.ProfileItems.Remove(desktop);
                vm.ProfileItems.Remove(wasd);

                // Each other wording of the In front now line.
                drawing.Picture(ground, "profiles-in-front-none.png", width, height, () =>
                {
                    vm.IsForegroundMatched = false;
                    vm.ForegroundProfileName = null;
                    vm.ForegroundExeName = "explorer.exe";
                });
                drawing.Picture(ground, "profiles-in-front-no-games.png", width, height, () =>
                {
                    rocket.Executables = null;
                    rocket.ExecutablePaths = null;
                });
                rocket.ExecutablePaths = @"C:\Games\ForzaHorizon5\ForzaHorizon5.exe";
                rocket.Executables = "ForzaHorizon5.exe";
                drawing.Picture(ground, "profiles-switching-off.png", width, height, () => vm.EnableAutoProfileSwitching = false);
                vm.EnableAutoProfileSwitching = true;

                // Another profile active, and a profile with no controllers.
                drawing.Picture(ground, "profiles-another-active.png", width, height, () =>
                {
                    standard.IsActive = false;
                    rocket.IsActive = true;
                });
                rocket.IsActive = false;
                standard.IsActive = true;
                drawing.Picture(ground, "profiles-no-controllers.png", width, height, () => rocket.SetTopology(null, null));
                rocket.SetTopology(new[] { true, true }, new[] { (int)VirtualControllerType.Xbox, (int)VirtualControllerType.Extended });

                // The shortcut while it records, and on Specific Profile with
                // the profile picker after its action.
                var held = next.Data.TriggerEntries;
                drawing.Picture(ground, "profiles-shortcut-recording.png", width, height, () =>
                {
                    next.Data.TriggerEntries = null;
                    next.IsRecording = true;
                    next.RecordingCountdown = 4;
                });
                next.Data.TriggerEntries = held;
                next.CancelRecording();
                drawing.Picture(ground, "profiles-shortcut-specific.png", width, height, () => next.SwitchMode = SwitchProfileMode.Specific);
                drawing.Picture(ground, "profiles-shortcut-specific-900.png", Least, 1500);
                next.SwitchMode = SwitchProfileMode.Next;
                // No buttons recorded, and no shortcut at all.
                drawing.Picture(ground, "profiles-shortcut-none.png", width, height, () =>
                {
                    next.Data.TriggerEntries = null;
                    next.NotifyComboChanged();
                });
                next.Data.TriggerEntries = held;
                next.NotifyComboChanged();
                drawing.Picture(ground, "profiles-no-shortcuts.png", width, height, () => vm.ProfileShortcuts.Clear());
                vm.ProfileShortcuts.Add(next);

                // External Control open. The fold is closed again after, since
                // its state is the app's and outlives the page.
                var external = (Expander)page.FindName("ExternalControlFold");
                drawing.Picture(ground, "profiles-external-control-open.png", width, height, () => external.IsExpanded = true);
                external.IsExpanded = false;
            });
        }

        /// <summary>Get Profiles on each tab, drawn from a window shown off
        /// every screen and never activated: a window has no picture until it
        /// is shown. Community stays cold, since its other states reach the
        /// network.</summary>
        [Fact]
        public void DrawGetProfiles()
        {
            Run("get-profiles", drawing =>
            {
                var settings = new SettingsViewModel { EnableCommunityConfigLookup = false };
                foreach (var tab in new[] { GetProfilesTab.Starters, GetProfilesTab.Community, GetProfilesTab.File })
                {
                    var dialog = new GetProfilesDialog(settings, tab);
                    drawing.PictureWindow(dialog, "get-profiles-" + tab.ToString().ToLowerInvariant() + ".png");
                }
            });
        }

        /// <summary>The Settings boards' page: each group, the Battery Alerts
        /// More row and HidHide Options open, the search for "notify" and a
        /// search that matches nothing, with the boards' data: HidHide,
        /// HIDMaestro and BthPS3 installed, MIDI on Windows' own API, no
        /// SteamVR, and analog keyboards and Bliss-Box adapters on with
        /// nothing found. The switches that reach a static put it back
        /// after.</summary>
        [Fact]
        public void DrawSettings()
        {
            Run("settings", drawing =>
            {
                bool analog = PadForge.Common.Input.AnalogKeyboardRuntime.Enabled;
                bool bliss = PadForge.Common.Input.BlissBoxRuntime.Enabled;
                bool screens = PadForge.Common.Input.KeyboardScreensRuntime.Enabled;
                bool gkeys = PadForge.Common.Input.LogitechGKeysRuntime.Enabled;
                bool handheld = PadForge.Common.Input.HandheldButtonRegistry.FeatureEnabled;
                bool falcon = PadForge.Common.Input.Falcon.FalconRuntime.Enabled;
                try
                {
                    var vm = new SettingsViewModel
                    {
                        IsHidHideInstalled = true, HidHideVersion = "1.5.230",
                        IsHIDMaestroInstalled = true, HIDMaestroVersion = "2.1.0",
                        IsBthPS3Installed = true,
                        AnalogKeyboardsEnabled = true, BlissBoxEnabled = true, KeyboardScreensEnabled = true,
                        SettingsFilePath = @"D:\Tools\PadForge\PadForge.xml",
                        ApplicationVersion = "6.0.0", RuntimeVersion = "10.0.13", SdlVersion = "SDL 3.5.0",
                    };
                    vm.ActiveMidiApi = PadForge.Common.Input.MidiApiKind.InBox;
                    vm.AnalogKeyboardsStatus = PadForge.Resources.Strings.Strings.Instance.Settings_AnalogKeyboardsStatus_None;
                    vm.BlissBoxStatus = PadForge.Resources.Strings.Strings.Instance.Settings_BlissBoxStatus_None;
                    vm.VersionNumber = "6.0.0";
                    vm.SetUpdateStatus(() => "PadForge 6.0.0 is up to date.", upToDate: true);

                    var page = new SettingsPage { DataContext = vm };
                    var ground = drawing.Ground(page);
                    drawing.Step("the page is made");
                    // A page beside the sidebar of 264: the board's 1440 frame here,
                    // then the window's own 1100 and its least, 900.
                    double width = 1440 - 264, height = 1000 - 52;

                    foreach (SettingsGroup group in Enum.GetValues(typeof(SettingsGroup)))
                    {
                        string file = "settings-" + group.ToString().ToLowerInvariant() + ".png";
                        drawing.Picture(ground, file, width, height, () => vm.SelectedSettingsGroup = group);
                        if (group == SettingsGroup.Input)
                        {
                            // The More row open, and closed again after, since its
                            // state is the app's and outlives the page.
                            var more = (Expander)page.FindName("BatteryMore");
                            drawing.Picture(ground, "settings-input-more.png", width, height, () => more.IsExpanded = true);
                            more.IsExpanded = false;
                        }
                        if (group == SettingsGroup.Drivers)
                        {
                            var options = (Expander)page.FindName("HidHideOptions");
                            drawing.Picture(ground, "settings-drivers-options.png", width, height, () => options.IsExpanded = true);
                            options.IsExpanded = false;
                        }
                        if (group == SettingsGroup.Data)
                        {
                            // What Is Sent open: the full account under it.
                            var sent = (System.Windows.Controls.Primitives.ToggleButton)page.FindName("WhatIsSentToggle");
                            drawing.Picture(ground, "settings-data-sent.png", width, height, () => sent.IsChecked = true);
                            sent.IsChecked = false;
                        }
                    }

                    // The window's own width, and its least, where the groups stand
                    // over the cards.
                    double usual = 1100 - 264, narrow = 900 - 264, tall = 1400 - TitleBar;
                    drawing.Picture(ground, "settings-general-1100.png", usual, tall, () => vm.SelectedSettingsGroup = SettingsGroup.General);
                    drawing.Picture(ground, "settings-hardware-1100.png", usual, tall, () => vm.SelectedSettingsGroup = SettingsGroup.Hardware);
                    drawing.Picture(ground, "settings-general-900.png", narrow, tall, () => vm.SelectedSettingsGroup = SettingsGroup.General);
                    drawing.Picture(ground, "settings-hardware-900.png", narrow, tall, () => vm.SelectedSettingsGroup = SettingsGroup.Hardware);
                    drawing.Picture(ground, "settings-drivers-900.png", narrow, tall, () => vm.SelectedSettingsGroup = SettingsGroup.Drivers);

                    // The Updates card before any check, and with an update waiting.
                    drawing.Picture(ground, "settings-updates-unchecked.png", width, height, () =>
                    {
                        vm.SelectedSettingsGroup = SettingsGroup.General;
                        vm.SetUpdateStatus(null);
                    });
                    drawing.Picture(ground, "settings-updates-waiting.png", width, height, () =>
                    {
                        vm.IsUpdateAvailable = true;
                        vm.SetUpdateStatus(() => "PadForge 6.0.1 is available.");
                    });
                    drawing.Picture(ground, "settings-updates-waiting-900.png", narrow, tall);
                    vm.IsUpdateAvailable = false;
                    vm.SetUpdateStatus(() => "PadForge 6.0.0 is up to date.", upToDate: true);

                    // The search, and a search with no match.
                    drawing.Picture(ground, "settings-search-notify.png", width, height, () => vm.SettingsSearchText = "notify");
                    drawing.Picture(ground, "settings-search-none.png", width, height, () => vm.SettingsSearchText = "xyz");
                    vm.SettingsSearchText = string.Empty;
                }
                finally
                {
                    PadForge.Common.Input.AnalogKeyboardRuntime.Enabled = analog;
                    PadForge.Common.Input.BlissBoxRuntime.Enabled = bliss;
                    PadForge.Common.Input.KeyboardScreensRuntime.Enabled = screens;
                    PadForge.Common.Input.LogitechGKeysRuntime.Enabled = gkeys;
                    PadForge.Common.Input.HandheldButtonRegistry.FeatureEnabled = handheld;
                    PadForge.Common.Input.Falcon.FalconRuntime.Enabled = falcon;
                }
            });
        }

        /// <summary>The About board's page: PadForge 6.0.0 up to date over the
        /// first five credits. Then what the board does not draw: the
        /// credits opened, found by a query and matching none, an update
        /// waiting, the 900 frame, where the buttons start a line of their
        /// own, and the light theme, after which the dark one comes
        /// back.</summary>
        [Fact]
        public void DrawAbout()
        {
            Run("about", drawing =>
            {
                var strings = PadForge.Resources.Strings.Strings.Instance;
                var vm = new SettingsViewModel { ApplicationVersion = "6.0.0 (r5210, 1a2b3c4)", VersionNumber = "6.0.0" };
                vm.SetUpdateStatus(() => string.Format(strings.Update_UpToDate_Format, "6.0.0"), upToDate: true);
                var page = new AboutPage { DataContext = vm };
                var ground = drawing.Ground(page);
                drawing.Step("the page is made");
                double width = 1280 - PaneAndEdge, height = 1000 - TitleBar;
                drawing.Picture(ground, "about.png", width, height);

                // The credits' card at the top of the view, with the page's
                // own space over it.
                var scroller = (ScrollViewer)page.Content;
                var list = (StackPanel)page.FindName("CreditsList");
                var card = (FrameworkElement)((FrameworkElement)list.Parent).Parent;
                var search = (PadForge.Controls.SearchField)page.FindName("CreditsSearch");
                var more = (Wpf.Ui.Controls.Button)page.FindName("ShowAllCreditsButton");
                void AtCredits()
                {
                    ground.UpdateLayout();
                    double top = card.TransformToAncestor((Visual)scroller.Content).Transform(new Point()).Y;
                    scroller.ScrollToVerticalOffset(top - 28);
                }
                drawing.Picture(ground, "about-credits-open.png", width, height, () =>
                {
                    more.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    AtCredits();
                });
                more.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                drawing.Picture(ground, "about-credits-found.png", width, height, () =>
                {
                    search.Text = "dualshock";
                    AtCredits();
                });
                drawing.Picture(ground, "about-credits-none.png", width, height, () =>
                {
                    search.Text = "zzqx";
                    AtCredits();
                });
                search.Text = string.Empty;

                drawing.Picture(ground, "about-update-waiting.png", width, height, () =>
                {
                    scroller.ScrollToTop();
                    vm.IsUpdateAvailable = true;
                    vm.SetUpdateStatus(() => string.Format(strings.Update_Available_Format, "6.0.1"));
                });
                vm.IsUpdateAvailable = false;
                vm.SetUpdateStatus(() => string.Format(strings.Update_UpToDate_Format, "6.0.0"), upToDate: true);
                drawing.Picture(ground, "about-900.png", 900 - PaneAndEdge, height);

                try
                {
                    drawing.Picture(ground, "about-light.png", width, height, () =>
                    {
                        PageRenderDump.ApplyTheme(ApplicationTheme.Light);
                        PadForge.Common.EmberTheme.ApplyAccent();
                    });
                }
                finally
                {
                    PageRenderDump.ApplyTheme(ApplicationTheme.Dark);
                    PadForge.Common.EmberTheme.ApplyAccent();
                }
            });
        }

        /// <summary>A dialog made by a constructor the app keeps to itself:
        /// the one whose parameters take <paramref name="arguments"/>.</summary>
        private static T Make<T>(params object[] arguments) where T : Window
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            foreach (var constructor in typeof(T).GetConstructors(any))
            {
                var parameters = constructor.GetParameters();
                if (parameters.Length != arguments.Length) continue;
                bool fits = true;
                for (int i = 0; i < parameters.Length && fits; i++)
                    fits = arguments[i] == null
                        ? !parameters[i].ParameterType.IsValueType
                        : parameters[i].ParameterType.IsInstanceOfType(arguments[i]);
                if (fits) return (T)constructor.Invoke(arguments);
            }
            throw new InvalidOperationException("No constructor of " + typeof(T).Name + " takes these arguments.");
        }

        /// <summary>A removal window with one row the removal takes and one
        /// it keeps, shown the way a preview shows its rows.</summary>
        private static Window RemovalWith(Type window)
        {
            const System.Reflection.BindingFlags own = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var dialog = (Window)window.GetConstructors(own)[0].Invoke(new object[] { null });
            var rowType = window.GetNestedType("EntryRow");
            var rows = (System.Collections.IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(rowType));
            foreach (var (title, location, outcome, removed, detail) in new[]
                     {
                         ("Driver package", "oem5.inf (hidmaestro.inf)", "Removed", true, (string)null),
                         ("Device node", @"ROOT\HIDMAESTRO\0000", "Removed", true, null),
                         ("Service setting", @"HKLM\SYSTEM\CurrentControlSet\Services\xinputhid", "Kept", false, "Stays: HIDMaestro never recorded its earlier value"),
                     })
            {
                object row = Activator.CreateInstance(rowType);
                rowType.GetProperty("Kind").SetValue(row, title);
                rowType.GetProperty("Title").SetValue(row, title);
                rowType.GetProperty("Location").SetValue(row, location);
                rowType.GetProperty("Outcome").SetValue(row, outcome);
                rowType.GetProperty("IsRemoved").SetValue(row, removed);
                rowType.GetProperty("Detail").SetValue(row, detail);
                rows.Add(row);
            }
            window.GetMethod("ShowEntries", own).Invoke(dialog, new object[] { rows });
            return dialog;
        }

        /// <summary>Each dialog on the shared frame (#574), with the data
        /// its board draws, from a window shown off every screen and never
        /// activated. A dialog that listens to a device or the network is
        /// drawn as it opens, before anything answers.</summary>
        [Fact]
        public void DrawDialogs()
        {
            Run("dialogs", drawing =>
            {
                // The Confirm board.
                drawing.PictureWindow(Make<ConfirmDialog>("Delete Rocket League?",
                    "The profile and its auto-switch rule are removed. This cannot be undone.", "Delete", null, ConfirmKind.Destructive, null),
                    "dialog-confirm-delete.png");
                drawing.PictureWindow(Make<ConfirmDialog>("Updates for Community Configs",
                    "2 of your community configs have a newer version.", "Update", "Star Courier\nDeep Rock", ConfirmKind.Primary, "Not Now"),
                    "dialog-confirm-primary.png");
                drawing.PictureWindow(Make<ConfirmDialog>("Cleanup Did Not Finish",
                    "Some old driver files are still on this PC.", null, null, ConfirmKind.Destructive, null),
                    "dialog-confirm-notice.png");
                drawing.PictureWindow(Make<KeepDriverDialog>("Corsair K70 RGB PRO", 10, false, false), "dialog-keep.png");
                drawing.PictureWindow(Make<BlissBoxPlayerDialog>(2, "Bliss-Box 4-Play, port 2"), "dialog-player-number.png");
                drawing.PictureWindow(RemovalWith(typeof(UninstallHIDMaestroDialog)), "dialog-uninstall-hidmaestro.png");
                drawing.PictureWindow(RemovalWith(typeof(UninstallBthPS3Dialog)), "dialog-uninstall-bthps3.png");
                drawing.PictureWindow(new CopyFromDialog(new[]
                {
                    new CopyFromDialog.DeviceEntry { Name = "Xbox #2", Detail = "DualSense Wireless Controller" },
                    new CopyFromDialog.DeviceEntry { Name = "Keyboard + Mouse #1", Detail = "Xbox Series X Controller" },
                    new CopyFromDialog.DeviceEntry { Name = "8BitDo Pro 2", Detail = "Not assigned, keeps mappings from before" },
                }, "Replace the mappings and per-device tuning of Xbox #1 with another controller's."), "dialog-copy-from.png");
                drawing.PictureWindow(new PickSoundDialog(new[]
                {
                    new PickSoundDialog.Item("alert.wav", "Arcade Pack", "pfsound://Arcade Pack/alert.wav"),
                    new PickSoundDialog.Item("chime.wav", "Arcade Pack", "pfsound://Arcade Pack/chime.wav"),
                    new PickSoundDialog.Item("level-up.wav", "Arcade Pack", "pfsound://Arcade Pack/level-up.wav"),
                }, allowBrowse: true, previewSlot: 0, previewVolume: 100, preselectValue: "pfsound://Arcade Pack/chime.wav"),
                    "dialog-pick-sound.png");

                // The Record board.
                var edit = new ProfileDialog();
                edit.LoadForEdit("Rocket League", new[] { @"C:\Games\rocketleague\Binaries\Win64\RocketLeague.exe" });
                drawing.PictureWindow(edit, "dialog-profile-edit.png");
                var games = new ProfileDialog();
                games.LoadForEdit("Shooters", new[]
                {
                    @"C:\Games\Apex\r5apex.exe", @"C:\Games\Halo Infinite\HaloInfinite.exe",
                    @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe",
                }, pollingOverrideMs: 2);
                drawing.PictureWindow(games, "dialog-profile-edit-games.png");
                var fresh = new ProfileDialog();
                fresh.ForNew();
                drawing.PictureWindow(fresh, "dialog-profile-new.png");

                const string pad = "7d0a2c5e-0000-0000-0000-000000000001";
                var inputs = new[]
                {
                    new InputChoice { Descriptor = "Button 4", DisplayName = "Left Bumper", DeviceGuid = pad, DeviceLabel = "Xbox Series X Controller" },
                    new InputChoice { Descriptor = "Button 5", DisplayName = "Right Bumper", DeviceGuid = pad, DeviceLabel = "Xbox Series X Controller" },
                    new InputChoice { Descriptor = "Axis 4", DisplayName = "Left Trigger", DeviceGuid = pad, DeviceLabel = "Xbox Series X Controller" },
                };
                var aim = new PadForge.Engine.Data.ShiftActivator
                {
                    LayerName = "Aim", LayerMask = "Aim", Mode = "Hold", Kind = "Button", Descriptor = "Button 4", DeviceGuid = pad,
                };
                var others = new[] { new PadForge.Engine.Data.ShiftActivator { LayerName = "Menu", LayerMask = "Menu" } };
                drawing.PictureWindow(new ShiftActivatorDialog(inputs, aim, others), "dialog-shift-layer.png");
                drawing.PictureWindow(new ShiftActivatorDialog(inputs, null, others), "dialog-shift-layer-new.png");
                var sprint = new PadForge.Engine.Data.ShiftActivator
                {
                    LayerName = "Sprint", LayerMask = "Sprint", Mode = "Toggle", Kind = "Chord", Descriptor = "Button 4", DeviceGuid = pad,
                    ChordSecondDescriptor = "Button 5", ChordSecondDeviceGuid = pad, DelayMs = 250, AutoCancelMs = 5000, Color = "#FF6B2C",
                };
                drawing.PictureWindow(new ShiftActivatorDialog(inputs, sprint, others), "dialog-shift-layer-more.png");
                var trigger = new PadForge.Engine.Data.ShiftActivator
                {
                    LayerName = "Scope", LayerMask = "Scope", Mode = "Hold", Kind = "Axis", Descriptor = "Axis 4", DeviceGuid = pad, AxisThreshold = 0.6,
                };
                drawing.PictureWindow(new ShiftActivatorDialog(inputs, trigger, others), "dialog-shift-layer-axis.png");
                var ring = new PadForge.Engine.Data.ShiftActivator
                {
                    LayerName = "Weapons", LayerMask = "Weapons", Mode = "Cycle", Kind = "Button", Descriptor = "Button 5", DeviceGuid = pad,
                    CyclePrevDescriptor = "Button 4", CyclePrevDeviceGuid = pad, CycleLayers = "Menu|Weapons",
                };
                drawing.PictureWindow(new ShiftActivatorDialog(inputs, ring, others), "dialog-shift-layer-cycle.png");
                drawing.PictureWindow(new ShiftActivatorDialog("Base", "#7CC7EA", ""), "dialog-shift-layer-base.png");

                // The four dialogs that list what a registry holds. Each
                // registry takes the board's rows for its pictures and gets
                // its own back after.
                var tags = PadForge.Common.Input.NfcTagRegistry.SaveRegistry();
                try
                {
                    PadForge.Common.Input.NfcTagRegistry.LoadRegistry(new[] { ("04A2B91C7F6180", "Mario", 1), ("04D7715A3C2B80", "Link", 2) });
                    drawing.PictureWindow(new RegisterNfcTagDialog(), "dialog-nfc-tags.png");
                    // A tag read while the dialog is open.
                    drawing.PictureWindow(new RegisterNfcTagDialog(), "dialog-nfc-tags-read.png",
                        () => PadForge.Common.Input.NfcTagRegistry.RaiseControllerTag("04F31B2A9C4D80"));
                    PadForge.Common.Input.NfcTagRegistry.LoadRegistry(Array.Empty<(string, string, int)>());
                    drawing.PictureWindow(new RegisterNfcTagDialog(), "dialog-nfc-tags-none.png");
                }
                finally { PadForge.Common.Input.NfcTagRegistry.LoadRegistry(tags); }

                var phrases = PadForge.Common.Input.VoicePhraseRegistry.SaveRegistry();
                bool listening = VoiceMacroService.Enabled;
                try
                {
                    PadForge.Common.Input.VoicePhraseRegistry.LoadRegistry(new[] { ("reload", "Reload", 1), ("open map", "Map", 2) });
                    VoiceMacroService.Enabled = true;
                    var heard = new RegisterVoicePhraseDialog();
                    drawing.PictureWindow(heard, "dialog-voice-macros.png",
                        () => heard.ShowHeard("Headset Microphone", "reload", 0.93f, fired: true));
                    var ignored = new RegisterVoicePhraseDialog();
                    drawing.PictureWindow(ignored, "dialog-voice-macros-ignored.png",
                        () => ignored.ShowHeard("Headset Microphone", "open map", 0.41f, fired: false));
                    VoiceMacroService.Enabled = false;
                    drawing.PictureWindow(new RegisterVoicePhraseDialog(), "dialog-voice-macros-off.png");
                }
                finally
                {
                    VoiceMacroService.Enabled = listening;
                    PadForge.Common.Input.VoicePhraseRegistry.LoadRegistry(phrases);
                }

                var words = PadForge.Common.Input.Twitch.ChatCommandRegistry.SaveRegistry();
                try
                {
                    PadForge.Common.Input.Twitch.ChatCommandRegistry.LoadRegistry(new[] { ("a", 1), ("b", 2), ("up", 3) });
                    drawing.PictureWindow(new ChatCommandsDialog(), "dialog-chat-commands.png");
                }
                finally { PadForge.Common.Input.Twitch.ChatCommandRegistry.LoadRegistry(words); }

                var learned = PadForge.Common.Input.HandheldButtonRegistry.SaveRegistry();
                string machine = PadForge.Common.Input.HandheldButtonRegistry.MachineKey;
                try
                {
                    PadForge.Common.Input.HandheldButtonRegistry.LoadRegistry(new[]
                    {
                        new PadForge.Common.Input.HandheldButtonRegistry.Entry { Name = "Armoury Crate", Button = 0, Keys = new[] { 0x7C } },
                        new PadForge.Common.Input.HandheldButtonRegistry.Entry { Name = "Command Center", Button = 1, Keys = new[] { 0x7D } },
                    }, machine);
                    drawing.PictureWindow(new LearnHandheldButtonDialog(), "dialog-hidden-buttons.png");
                    PadForge.Common.Input.HandheldButtonRegistry.LoadRegistry(
                        Array.Empty<PadForge.Common.Input.HandheldButtonRegistry.Entry>(), machine);
                    drawing.PictureWindow(new LearnHandheldButtonDialog(), "dialog-hidden-buttons-none.png");
                }
                finally { PadForge.Common.Input.HandheldButtonRegistry.LoadRegistry(learned, machine); }

                // Import Profile, of the Dialogs board.
                drawing.PictureWindow(new ImportConfigDialog(@"C:\Profiles\Star Courier.rewasd", ImportProfileDialogTests.StarCourier()),
                    "dialog-import-profile.png");

                // The Links board.
                const System.Reflection.BindingFlags own = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var say = typeof(PairDeviceDialog).GetMethod("SetStatus", own);
                var pairing = new PairDeviceDialog(new SettingsViewModel());
                drawing.PictureWindow(pairing, "dialog-pair-wii.png");
                var searching = new PairDeviceDialog(new SettingsViewModel());
                drawing.PictureWindow(searching, "dialog-pair-wii-searching.png", () =>
                {
                    typeof(PairDeviceDialog).GetMethod("ShowWorking", own).Invoke(searching, new object[] { true });
                    say.Invoke(searching, new object[] { PadForge.Resources.Strings.Strings.Instance.WiiPair_Searching, true, false, false });
                });
                var failed = new PairDeviceDialog(new SettingsViewModel());
                drawing.PictureWindow(failed, "dialog-pair-ds3-failed.png", () =>
                {
                    ((ComboBox)failed.FindName("FamilyCombo")).SelectedIndex = 1;
                    say.Invoke(failed, new object[] { PadForge.Resources.Strings.Strings.Instance.Ds3Pair_Working, true, false, false });
                    say.Invoke(failed, new object[] { PadForge.Resources.Strings.Strings.Instance.Ds3Pair_Failed, false, false, true });
                });
                var move = new PairDeviceDialog(new SettingsViewModel());
                drawing.PictureWindow(move, "dialog-pair-move.png", () => ((ComboBox)move.FindName("FamilyCombo")).SelectedIndex = 2);
                var serialSettings = new SettingsViewModel();
                serialSettings.SerialControllers.Add(new PadForge.Common.Input.SerialControllerEntry { Port = @"FTDIBUS\VID_0403+PID_6001+A\0000", PortName = "COM3", Protocol = "stinger" });
                var serial = new PairDeviceDialog(serialSettings);
                drawing.PictureWindow(serial, "dialog-pair-serial.png", () =>
                {
                    ((ComboBox)serial.FindName("FamilyCombo")).SelectedIndex = 3;
                    say.Invoke(serial, new object[] { string.Format(PadForge.Resources.Strings.Strings.Instance.SerialPair_AddedFormat, "Stinger", "COM3"), false, true, false });
                });
                var djiSettings = new SettingsViewModel();
                djiSettings.DjiRemoteHosts.Add("192.168.7.251:40007");
                var dji = new PairDeviceDialog(djiSettings);
                drawing.PictureWindow(dji, "dialog-pair-dji.png", () => ((ComboBox)dji.FindName("FamilyCombo")).SelectedIndex = 4);

                drawing.PictureWindow(new TouchpadGestureRecorderDialog(), "dialog-gesture.png");
                var stroke = new TouchpadGestureRecorderDialog();
                drawing.PictureWindow(stroke, "dialog-gesture-drawing.png", () =>
                {
                    // The board's check mark, drawn as a finger on the surface would.
                    var begin = typeof(TouchpadGestureRecorderDialog).GetMethod("BeginFinger", own, null, new[] { typeof(int), typeof(Point) }, null);
                    var go = typeof(TouchpadGestureRecorderDialog).GetMethod("ContinueFinger", own, null, new[] { typeof(int), typeof(Point) }, null);
                    begin.Invoke(stroke, new object[] { 1, new Point(120, 110) });
                    go.Invoke(stroke, new object[] { 1, new Point(190, 170) });
                    go.Invoke(stroke, new object[] { 1, new Point(380, 40) });
                    ((TextBox)stroke.FindName("NameBox")).Text = "Check";
                });

                drawing.PictureWindow(new RemoteLinkPairDialog("482915", "A1B2C3D4E5F6071829304A5B6C7D8E9F"), "dialog-remote-pair.png");
                drawing.PictureWindow(new RemoteLinkPasswordDialog(true, PadForge.Resources.Strings.Strings.Instance.RemoteLink_PasswordSetPrompt), "dialog-remote-password-set.png");
                var mismatch = new RemoteLinkPasswordDialog(true, PadForge.Resources.Strings.Strings.Instance.RemoteLink_PasswordSetPrompt);
                drawing.PictureWindow(mismatch, "dialog-remote-password-mismatch.png", () =>
                {
                    ((PasswordBox)mismatch.FindName("PwBox")).Password = "one";
                    ((PasswordBox)mismatch.FindName("ConfirmBox")).Password = "two";
                    ((System.Windows.Controls.Primitives.ButtonBase)mismatch.FindName("OkButton")).RaiseEvent(
                        new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                });
                drawing.PictureWindow(new RemoteLinkPasswordDialog(false, PadForge.Resources.Strings.Strings.Instance.RemoteLink_PasswordUnlockPrompt), "dialog-remote-password-unlock.png");

                PadForge.Engine.RemoteLink.LinkAssignmentRequest asked = null;
                var channel = new PadForge.Engine.RemoteLink.LinkAssignmentChannel("peer", () => true, () => true, _ => null,
                    bytes =>
                    {
                        if (PadForge.Engine.RemoteLink.LinkAssignmentCodec.TryDecode(bytes, out var request, out _)) asked = request;
                        return true;
                    }, null);
                try
                {
                    var shared = new[]
                    {
                        new PadForge.Engine.RemoteLink.RemotePeerDeviceInfo { PeerLocalDeviceId = "pad-1", Name = "DualSense Wireless Controller" },
                        new PadForge.Engine.RemoteLink.RemotePeerDeviceInfo { PeerLocalDeviceId = "pad-2", Name = "Xbox Series X GIP Controller" },
                    };
                    var assignments = new RemoteAssignmentsDialog("Living Room PC", channel, () => shared);
                    drawing.PictureWindow(assignments, "dialog-remote-assignments.png", () =>
                    {
                        if (asked == null) return;
                        channel.Receive(PadForge.Engine.RemoteLink.LinkAssignmentCodec.Encode(new PadForge.Engine.RemoteLink.LinkAssignmentReply(
                            asked.RequestId, asked.DeviceId, PadForge.Engine.RemoteLink.LinkAssignmentStatus.Ok, 7, "Default",
                            new[]
                            {
                                new PadForge.Engine.RemoteLink.LinkAssignmentSlot(0, "Xbox #1", true, true),
                                new PadForge.Engine.RemoteLink.LinkAssignmentSlot(1, "Xbox #2", false, true),
                            })));
                    });
                    drawing.PictureWindow(new RemoteAssignmentsDialog("Living Room PC", channel,
                        () => Array.Empty<PadForge.Engine.RemoteLink.RemotePeerDeviceInfo>()), "dialog-remote-assignments-none.png");
                }
                finally { channel.Close(); }

                var presets = new ManageProfilesDialog(new SettingsService(new MainViewModel()));
                drawing.PictureWindow(presets, "dialog-presets.png", () =>
                {
                    ((ListBox)presets.FindName("ImportedList")).ItemsSource = new[]
                    {
                        new ManageProfilesDialog.ImportedProfileRow { Id = "user-8bitdo-pro-2", Name = "8BitDo Pro 2" },
                        new ManageProfilesDialog.ImportedProfileRow { Id = "user-flight-stick", Name = "Flight Stick", Problem = "This profile has no HID descriptor, so no controller can be made from it." },
                    };
                    ((FrameworkElement)presets.FindName("ImportedBlock")).Visibility = Visibility.Visible;
                });
                drawing.PictureWindow(new ManageProfilesDialog(new SettingsService(new MainViewModel())), "dialog-presets-none.png");
            });
        }

        /// <summary>The board's two formats: a binary one with its Speed and
        /// Throttle fields, and a JSON one that needs a key.</summary>
        internal static System.Collections.Generic.List<PadForge.Common.Input.Telemetry.CustomTelemetryFormat> BoardFormats()
        {
            var rig = new PadForge.Common.Input.Telemetry.CustomTelemetryFormat { Name = "Rig Telemetry", Length = 232 };
            rig.Fields.Add(new PadForge.Common.Input.Telemetry.CustomTelemetryField
            {
                Target = PadForge.Common.Input.Telemetry.CustomTelemetryTarget.Speed,
                Offset = 0, Type = PadForge.Common.Input.Telemetry.CustomTelemetryType.F32, Scale = 3.6, Min = 0, Max = 300,
            });
            rig.Fields.Add(new PadForge.Common.Input.Telemetry.CustomTelemetryField
            {
                Target = PadForge.Common.Input.Telemetry.CustomTelemetryTarget.Throttle,
                Offset = 4, Type = PadForge.Common.Input.Telemetry.CustomTelemetryType.F32, Scale = 1, Min = 0, Max = 1,
            });
            rig.Fields.Add(new PadForge.Common.Input.Telemetry.CustomTelemetryField
            {
                Target = PadForge.Common.Input.Telemetry.CustomTelemetryTarget.Variable, Name = "Boost",
                Offset = 8, Type = PadForge.Common.Input.Telemetry.CustomTelemetryType.U16, Scale = 0.01, Min = 0, Max = 2.5,
            });
            var dash = new PadForge.Common.Input.Telemetry.CustomTelemetryFormat
            {
                Name = "Sim Dashboard", Kind = PadForge.Common.Input.Telemetry.CustomTelemetryKind.Json, Key = "telemetry",
            };
            dash.Fields.Add(new PadForge.Common.Input.Telemetry.CustomTelemetryField
            {
                Target = PadForge.Common.Input.Telemetry.CustomTelemetryTarget.Rpm, Key = "engine.rpm", Scale = 1, Min = 0, Max = 1,
            });
            dash.Fields.Add(new PadForge.Common.Input.Telemetry.CustomTelemetryField
            {
                Target = PadForge.Common.Input.Telemetry.CustomTelemetryTarget.Handbrake, Key = "inputs.handbrake", Scale = 1, Min = 0, Max = 1,
            });
            return new System.Collections.Generic.List<PadForge.Common.Input.Telemetry.CustomTelemetryFormat> { rig, dash };
        }

        /// <summary>A picture file of one color, for a dialog that shows a
        /// chosen picture.</summary>
        private static string PictureFile(string dir, string name, int width, int height, byte red, byte green, byte blue)
        {
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                // A diagonal band, so a pan and a zoom show in the preview.
                bool band = (i / 4 % width + i / 4 / width) / 40 % 2 == 0;
                pixels[i] = band ? blue : (byte)(blue / 3);
                pixels[i + 1] = band ? green : (byte)(green / 3);
                pixels[i + 2] = band ? red : (byte)(red / 3);
                pixels[i + 3] = 255;
            }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(dir, name);
            using (var file = File.Create(path)) encoder.Save(file);
            return path;
        }

        /// <summary>A square of one color, as an icon package's picture.</summary>
        private static ImageSource Swatch(byte red, byte green, byte blue)
        {
            var pixels = new byte[16 * 16 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = blue;
                pixels[i + 1] = green;
                pixels[i + 2] = red;
                pixels[i + 3] = 255;
            }
            var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>The dialogs of the Dialogs-Large and Dialogs-Screens
        /// boards (#574): Custom Formats, the keyboard screen and Screen
        /// Overlay dialog, Dreamcast Screen, Map Key Lights and the icon
        /// picker, each in its states.</summary>
        [Fact]
        public void DrawScreenDialogs()
        {
            Run("screen dialogs", drawing =>
            {
                const System.Reflection.BindingFlags own = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

                // Custom Formats, of the Dialogs-Large board.
                drawing.PictureWindow(new CustomTelemetryFormatsDialog(BoardFormats()), "dialog-custom-formats.png");
                var json = new CustomTelemetryFormatsDialog(BoardFormats());
                drawing.PictureWindow(json, "dialog-custom-formats-json.png", () => ((ListBox)json.FindName("FormatList")).SelectedIndex = 1);
                var gears = BoardFormats();
                gears[0].UseGearValues = true;
                gears[0].GearReverse = 0;
                gears[0].GearNeutral = 11;
                gears[0].BigEndian = true;
                drawing.PictureWindow(new CustomTelemetryFormatsDialog(gears), "dialog-custom-formats-more.png");
                var twice = BoardFormats();
                twice[1].Name = twice[0].Name;
                var problem = new CustomTelemetryFormatsDialog(twice);
                drawing.PictureWindow(problem, "dialog-custom-formats-problem.png", () =>
                    typeof(CustomTelemetryFormatsDialog).GetMethod("SaveButton_Click", own)
                        .Invoke(problem, new[] { problem.FindName("SaveButton"), new RoutedEventArgs() }));
                drawing.PictureWindow(new CustomTelemetryFormatsDialog(null), "dialog-custom-formats-none.png");

                // The keyboard screen and Screen Overlay dialog, of the
                // Dialogs-Screens board.
                var screens = new KeyboardScreenService(new SettingsViewModel(), null);
                var overlay = PadForge.Common.Input.KeyboardScreens.KeyboardScreenInfo.Overlay(1);
                object window = (Size?)new Size(320, 240);
                KeyboardScreenDialog Screen(PadForge.Common.Input.KeyboardScreens.KeyboardScreenInfo screen, KeyboardScreenData data, string profile = "Rocket League")
                    => Make<KeyboardScreenDialog>(screens, screen, data, profile, null, window);
                drawing.PictureWindow(Screen(overlay, new KeyboardScreenData { Mode = KeyboardScreenMode.Clock }), "dialog-screen-overlay-clock.png");
                drawing.PictureWindow(Screen(overlay, new KeyboardScreenData { Mode = KeyboardScreenMode.Lightbar }), "dialog-screen-overlay-lightbar.png");
                drawing.PictureWindow(Screen(overlay, new KeyboardScreenData { Mode = KeyboardScreenMode.ProfileName }), "dialog-screen-overlay-profile-name.png");
                drawing.PictureWindow(Screen(overlay, new KeyboardScreenData { Mode = KeyboardScreenMode.ProfilePicture },
                    "A Profile with a Name That Runs Well Past the Width of Its Button"), "dialog-screen-overlay-profile-picture.png");
                string picture = PictureFile(drawing.Folder, "chosen-picture.png", 400, 400, 0xFF, 0x6B, 0x2C);
                drawing.PictureWindow(Screen(overlay, new KeyboardScreenData { Mode = KeyboardScreenMode.Picture, Picture = picture, Zoom = 400, PanX = 120, PanY = 60 }),
                    "dialog-screen-overlay-picture.png");
                drawing.PictureWindow(Screen(new PadForge.Common.Input.KeyboardScreens.KeyboardScreenInfo("dump-mono",
                        PadForge.Common.Input.KeyboardScreens.KeyboardScreenKind.Monochrome, "G15", "G15"),
                    new KeyboardScreenData { Mode = KeyboardScreenMode.ProfileName }), "dialog-keyboard-screen-mono.png");
                drawing.PictureWindow(Screen(new PadForge.Common.Input.KeyboardScreens.KeyboardScreenInfo("dump-color",
                        PadForge.Common.Input.KeyboardScreens.KeyboardScreenKind.Color, "G19", "G19"),
                    new KeyboardScreenData { Mode = KeyboardScreenMode.Clock }), "dialog-keyboard-screen-color.png");
                drawing.PictureWindow(Screen(new PadForge.Common.Input.KeyboardScreens.KeyboardScreenInfo("dump-color",
                        PadForge.Common.Input.KeyboardScreens.KeyboardScreenKind.Color, "G19", "G19"),
                    new KeyboardScreenData { Mode = KeyboardScreenMode.Picture, Picture = picture }), "dialog-keyboard-screen-color-picture.png");

                // Dreamcast Screen.
                var vmu = new DreamcastScreenService(new SettingsViewModel(), null);
                using (var port = new PadForge.Common.Input.BlissBoxPort(@"\\?\hid#padforge-dump", 0x0D04, Guid.NewGuid(), 1))
                {
                    drawing.PictureWindow(Make<DreamcastScreenDialog>(vmu, port, new BlissBoxPortData { ScreenMode = DreamcastScreenMode.ProfileName }, "Rocket League", null),
                        "dialog-dreamcast.png");
                    drawing.PictureWindow(Make<DreamcastScreenDialog>(vmu, port, new BlissBoxPortData { ScreenMode = DreamcastScreenMode.Picture }, "Rocket League", null),
                        "dialog-dreamcast-picture.png");
                    drawing.PictureWindow(Make<DreamcastScreenDialog>(vmu, port, new BlissBoxPortData { ScreenMode = DreamcastScreenMode.ProfilePicture },
                        "A Profile with a Name That Runs Well Past the Width of Its Button", null), "dialog-dreamcast-profile-picture.png");
                }

                // Map Key Lights: a board with most of its keys named, idle and
                // then with a light lit.
                var lights = new PadForge.Common.Input.Peripherals.OutputPath(PadForge.Common.Input.Peripherals.OutputFamily.HidLightingUnit, "dialog-render-dump");
                PadForge.Common.Input.Peripherals.PeripheralLightMaps.ResetForTest();
                try
                {
                    var keys = new System.Collections.Generic.List<PadForge.Common.Input.Peripherals.LightInfo>();
                    // Letters, digits, Enter, Escape, Backspace, Tab, Space and the keys beside them.
                    for (int usage = 0x04; usage <= 0x38; usage++)
                        keys.Add(new PadForge.Common.Input.Peripherals.LightInfo(keys.Count, PadForge.Common.Input.Peripherals.LightKind.Key,
                            (ushort)usage, PadForge.Common.Input.Peripherals.LightKeySource.Device));
                    for (int unnamed = 0; unnamed < 6; unnamed++)
                        keys.Add(new PadForge.Common.Input.Peripherals.LightInfo(keys.Count, PadForge.Common.Input.Peripherals.LightKind.Key,
                            0, PadForge.Common.Input.Peripherals.LightKeySource.None));
                    PadForge.Common.Input.Peripherals.PeripheralLightMaps.Publish(lights,
                        new PadForge.Common.Input.Peripherals.LightMap(keys.ToArray(), "dialog-render-dump") { Product = "dump:keyboard" });
                    drawing.PictureWindow(new MapKeyLightsDialog(lights, "Corsair K70 RGB PRO"), "dialog-map-key-lights.png");
                    var listening = new MapKeyLightsDialog(lights, "Corsair K70 RGB PRO");
                    drawing.PictureWindow(listening, "dialog-map-key-lights-listening.png", () =>
                        ((MapKeyLightsViewModel)typeof(MapKeyLightsDialog).GetField("_vm", own).GetValue(listening)).Start());
                }
                finally
                {
                    PadForge.Common.Input.Peripherals.PeripheralLightMaps.ResetForTest();
                }

                // The icon picker: its Emoji tab as a menu cell opens it, the
                // same with an emoji held, and its Images tab with a package
                // of pictures, the second one held.
                var emoji = new IconPicker();
                emoji.Prepare("", PadForge.Resources.Strings.Strings.Instance.Menu_Icon_None);
                drawing.PictureControl(emoji, "icon-picker-emoji.png");
                var held = new IconPicker();
                held.Prepare("🚀", PadForge.Resources.Strings.Strings.Instance.Pad_Shift_Icon_Reset);
                drawing.PictureControl(held, "icon-picker-emoji-held.png");
                var none = new IconPicker();
                none.Prepare("", PadForge.Resources.Strings.Strings.Instance.Menu_Icon_None);
                typeof(IconPicker).GetMethod("SetTab", own).Invoke(none, new object[] { true });
                drawing.PictureControl(none, "icon-picker-images-none.png");
                var images = new IconPicker();
                images.Prepare("", PadForge.Resources.Strings.Strings.Instance.Menu_Icon_None);
                var pictures = new System.Collections.Generic.List<IconPicker.IconChoice>();
                for (int i = 0; i < 12; i++)
                    pictures.Add(new IconPicker.IconChoice("pficon://Racing Pack/" + i + ".png", "Picture " + (i + 1),
                        Swatch((byte)(60 + i * 16), (byte)(200 - i * 12), (byte)(90 + i * 13)), i == 1));
                var more = new System.Collections.Generic.List<IconPicker.IconChoice>();
                for (int i = 0; i < 40; i++)
                    more.Add(new IconPicker.IconChoice("pficon://Flight Deck/" + i + ".png", "Picture " + (i + 1),
                        Swatch((byte)(220 - i * 4), (byte)(120 + i * 3), (byte)(40 + i * 5)), false));
                ((ItemsControl)images.FindName("PackageGroups")).ItemsSource = new System.Collections.Generic.List<IconPicker.IconGroup>
                {
                    new IconPicker.IconGroup("Racing Pack", pictures),
                    new IconPicker.IconGroup("Flight Deck", more),
                };
                ((UIElement)images.FindName("NoPackagesText")).Visibility = Visibility.Collapsed;
                typeof(IconPicker).GetMethod("SetTab", own).Invoke(images, new object[] { true });
                drawing.PictureControl(images, "icon-picker-images.png");
            });
        }

        /// <summary>Runs <paramref name="body"/> beside the app's resources
        /// and theme, and draws into the folder PADFORGE_PAGE_RENDER_DIR
        /// names. Without that folder it does nothing.</summary>
        private static void Run(string what, Action<Drawing> body)
        {
            string dir = Environment.GetEnvironmentVariable("PADFORGE_PAGE_RENDER_DIR");
            if (string.IsNullOrEmpty(dir)) return;
            double scale = double.TryParse(Environment.GetEnvironmentVariable("PADFORGE_PAGE_RENDER_SCALE"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double asked) ? asked : 1.5;
            Directory.CreateDirectory(dir);
            WithApp((app, dispatcher) =>
            {
                var drawing = new Drawing(dir, scale, dispatcher) { App = app };
                // The light theme for the run when it is asked for, and the
                // dark one back after, which every other test stands in.
                bool light = PageRenderDump.PictureTheme() == ApplicationTheme.Light;
                if (light)
                {
                    PageRenderDump.ApplyTheme(ApplicationTheme.Light);
                    PadForge.Common.EmberTheme.ApplyAccent();
                }
                drawing.Step(what + ": the app's resources and theme are loaded");
                try { body(drawing); }
                catch (Exception error)
                {
                    drawing.Step(what + " failed: " + error);
                    throw;
                }
                finally
                {
                    if (light)
                    {
                        PageRenderDump.ApplyTheme(ApplicationTheme.Dark);
                        PadForge.Common.EmberTheme.ApplyAccent();
                    }
                }
            }, TimeSpan.FromMinutes(8), "The page render timed out. See steps.txt in the folder.");
        }

        /// <summary>Runs <paramref name="body"/> on an STA thread beside the
        /// app's resources in the dark theme, and puts the process back with
        /// no application after, as PageRenderDump does. A test that makes a
        /// page whose markup names the app's resources runs in it too, from
        /// the WpfApplicationSingleton collection.</summary>
        internal static void WithApp(Action<App, Dispatcher> body, TimeSpan limit, string timedOut)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                try
                {
                    PageRenderDump.SetResourceAssembly(typeof(App).Assembly);
                    var app = PageRenderDump.MakeAppThatNeverStarts(dispatcher);
                    app.InitializeComponent();
                    // The app's startup, which never runs here, shuts it down
                    // only when asked. Without that, a window the dump closes
                    // is the app's last, and the app shuts down under the next.
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    PageRenderDump.ApplyTheme(ApplicationTheme.Dark);
                    PadForge.Common.EmberTheme.ApplyAccent();
                    body(app, dispatcher);
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    try
                    {
                        PairDeviceDialogSerialTests.ReleaseApplication();
                        PageRenderDump.SetResourceAssembly(null);
                    }
                    catch (Exception error) { failure ??= error; }
                    dispatcher.InvokeShutdown();
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(limit), timedOut);
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>A folder to draw into, and the means to draw a page or a
        /// window there.</summary>
        private sealed class Drawing
        {
            private readonly string _dir;
            private readonly double _scale;
            private readonly Dispatcher _dispatcher;

            internal Drawing(string dir, double scale, Dispatcher dispatcher)
            {
                _dir = dir;
                _scale = scale;
                _dispatcher = dispatcher;
            }

            internal App App { get; set; }

            /// <summary>The folder the pictures go to.</summary>
            internal string Folder => _dir;

            /// <summary>Pictures a control that is no window, at the size it
            /// asks for, with <paramref name="around"/> of the app's ground
            /// on each side.</summary>
            internal void PictureControl(FrameworkElement control, string file, double around = 24)
            {
                var ground = Ground(new Border { Padding = new Thickness(around), Child = control });
                ground.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Picture(ground, file, Math.Ceiling(ground.DesiredSize.Width), Math.Ceiling(ground.DesiredSize.Height));
            }

            internal void Step(string what)
                => File.AppendAllText(Path.Combine(_dir, "steps.txt"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + what + Environment.NewLine);

            /// <summary>The page on the ground the window gives a page, with
            /// the text color and the font the main window gives everything
            /// in it. All three follow the theme.</summary>
            internal Border Ground(FrameworkElement page)
            {
                var ground = new Border { Child = page };
                ground.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
                ground.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
                ground.SetResourceReference(TextElement.FontFamilyProperty, "BodyFontFamily");
                return ground;
            }

            /// <summary>Shows a state, lets it settle, and pictures the page
            /// at the size, then logs its lost lookups.</summary>
            internal void Picture(FrameworkElement ground, string file, double width, double height, Action show = null)
            {
                show?.Invoke();
                Lay(ground, width, height);
                PageRenderDump.Settle(_dispatcher);
                Lay(ground, width, height);
                Save(ground, file, width, height);
                Lookups(ground, file);
            }

            /// <summary>Shows a window off every screen without activating
            /// it, pictures it on the app's ground once it settles, and
            /// closes it. <paramref name="whenShown"/> runs on the settled
            /// window, for a state that lasts a moment (a row lit for a
            /// second), and the picture is taken as soon as what it queued
            /// has run.</summary>
            internal void PictureWindow(Window window, string file, Action whenShown = null)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Left = -32000;
                window.Top = -32000;
                window.Show();
                try
                {
                    PageRenderDump.Settle(_dispatcher);
                    if (whenShown != null)
                    {
                        whenShown();
                        _dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    window.UpdateLayout();
                    double width = window.ActualWidth, height = window.ActualHeight;
                    var shot = new RenderTargetBitmap((int)Math.Round(width * _scale), (int)Math.Round(height * _scale),
                        96 * _scale, 96 * _scale, PixelFormats.Pbgra32);
                    shot.Render(window);
                    // The window's backdrop is the system's, which a picture
                    // does not hold, so the ground goes under it.
                    var composed = new DrawingVisual();
                    using (var context = composed.RenderOpen())
                    {
                        context.DrawRectangle((Brush)App.FindResource("SteelGroundBrush"), null, new Rect(0, 0, width, height));
                        context.DrawImage(shot, new Rect(0, 0, width, height));
                    }
                    var bitmap = new RenderTargetBitmap(shot.PixelWidth, shot.PixelHeight, 96 * _scale, 96 * _scale, PixelFormats.Pbgra32);
                    bitmap.Render(composed);
                    Write(bitmap, file);
                    Step(FormattableString.Invariant($"drew {file}, {width:0.#} by {height:0.#}"));
                    Lookups(window, file);
                }
                finally { window.Close(); }
            }

            private static void Lay(FrameworkElement ground, double width, double height)
            {
                ground.Measure(new Size(width, height));
                ground.Arrange(new Rect(0, 0, width, height));
                ground.UpdateLayout();
            }

            private void Save(FrameworkElement ground, string file, double width, double height)
            {
                var bitmap = new RenderTargetBitmap((int)Math.Round(width * _scale), (int)Math.Round(height * _scale),
                    96 * _scale, 96 * _scale, PixelFormats.Pbgra32);
                bitmap.Render(ground);
                Write(bitmap, file);
                Step(FormattableString.Invariant($"drew {file} at {width:0.#} by {height:0.#}"));
            }

            private void Write(BitmapSource bitmap, string file)
            {
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(_dir, file));
                png.Save(stream);
            }

            private void Lookups(DependencyObject root, string file)
            {
                var lost = PageRenderDump.LostLookups(root);
                File.AppendAllLines(Path.Combine(_dir, "lookups.txt"),
                    new[] { file + ": " + lost.Count + " lookups found nothing" }.Concat(lost.Select(l => "   " + l)));
            }
        }
    }
}
