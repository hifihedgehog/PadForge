using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Twitch;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The three dialogs of this window of work with every binding evaluated:
    /// Map Key Lights, Chat Commands and the Keep prompt. Page navigation never
    /// realizes a dialog, so these give the check the diagnostics harvest reads
    /// as BINDERR, on WPF's own trace. Each dialog gets a handle and a layout
    /// and is never shown, so nothing reaches the screen, the pointer or the
    /// focus of whoever is using the machine.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class DialogBindingTests
    {
        private sealed class BindingErrors : TraceListener
        {
            private readonly List<string> _lines = new();

            public string[] Lines
            {
                get { lock (_lines) return _lines.ToArray(); }
            }

            public void Clear()
            {
                lock (_lines) _lines.Clear();
            }

            public override void Write(string message) { }

            public override void WriteLine(string message)
            {
                lock (_lines) _lines.Add(message);
            }
        }

        /// <summary>The dialog is made inside the listening window: a binding
        /// with a source of its own is evaluated in the constructor.</summary>
        private static void AssertBindsCleanly(Func<Window> make)
        {
            var errors = new BindingErrors();
            PresentationTraceSources.Refresh();
            var source = PresentationTraceSources.DataBindingSource;
            var level = source.Switch.Level;
            source.Listeners.Add(errors);
            source.Switch.Level = SourceLevels.Error;
            Window dialog = null;
            try
            {
                // The positive control: a binding that names nothing is seen,
                // in the same window as the silence the dialog is held to.
                var control = new TextBlock { DataContext = new object() };
                control.SetBinding(TextBlock.TextProperty, "NoSuchProperty");
                Assert.NotEmpty(errors.Lines);
                errors.Clear();

                dialog = make();
                dialog.ShowInTaskbar = false;
                new WindowInteropHelper(dialog).EnsureHandle();
                // The template's root, so a dialog on the frame binds its
                // title, the line under it and its foot as well as its body.
                dialog.ApplyTemplate();
                var root = System.Windows.Media.VisualTreeHelper.GetChildrenCount(dialog) > 0
                    ? (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetChild(dialog, 0)
                    : (FrameworkElement)dialog.Content;
                root.Measure(new Size(900, 700));
                root.Arrange(new Rect(0, 0, 900, 700));
                root.UpdateLayout();
                dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Empty(errors.Lines);
            }
            finally
            {
                source.Listeners.Remove(errors);
                source.Switch.Level = level;
                dialog?.Close();
            }
        }

        [Fact]
        public void MapKeyLights_BindsCleanly() => CustomTelemetryFormatsDialogTests.RunWithApp(() =>
        {
            var path = new OutputPath(OutputFamily.HidLightingUnit, "dialog-binding-test");
            PeripheralLightMaps.ResetForTest();
            try
            {
                PeripheralLightMaps.Publish(path, new LightMap(new[]
                {
                    new LightInfo(0, LightKind.Key, 0x29, LightKeySource.Device),
                    new LightInfo(1, LightKind.Key, 0x04, LightKeySource.Device),
                }, "dialog-binding-test"));
                AssertBindsCleanly(() => new MapKeyLightsDialog(path, "Keyboard"));
            }
            finally
            {
                PeripheralLightMaps.ResetForTest();
            }
        });

        /// <summary>The dialogs of the Dialogs-Large and Dialogs-Screens
        /// boards: Custom Formats on a binary format and on a JSON one with
        /// More open, the screen dialog on an overlay and on a keyboard's
        /// screen, Dreamcast Screen, and the icon picker on each tab.</summary>
        [Fact]
        public void TheScreenBoardsDialogs_BindCleanly() => CustomTelemetryFormatsDialogTests.RunWithApp(() =>
        {
            AssertBindsCleanly(() => new CustomTelemetryFormatsDialog(PageRenderDumpPages.BoardFormats()));
            AssertBindsCleanly(() =>
            {
                var dialog = new CustomTelemetryFormatsDialog(PageRenderDumpPages.BoardFormats());
                ((ListBox)dialog.FindName("FormatList")).SelectedIndex = 1;
                Assert.True(((PadForge.Controls.MoreRow)dialog.FindName("FormatMore")).IsExpanded);
                return dialog;
            });
            AssertBindsCleanly(() => new CustomTelemetryFormatsDialog(null));

            foreach (var mode in new[] { PadForge.Services.KeyboardScreenMode.Clock, PadForge.Services.KeyboardScreenMode.Lightbar, PadForge.Services.KeyboardScreenMode.ProfilePicture })
                AssertBindsCleanly(() => ScreenDialogTests.Screen(PadForge.Common.Input.KeyboardScreens.KeyboardScreenInfo.Overlay(1),
                    new PadForge.Services.KeyboardScreenData { Mode = mode }));
            AssertBindsCleanly(() => ScreenDialogTests.Screen(
                new PadForge.Common.Input.KeyboardScreens.KeyboardScreenInfo("binding-test-mono", PadForge.Common.Input.KeyboardScreens.KeyboardScreenKind.Monochrome, "G15", "G15"),
                new PadForge.Services.KeyboardScreenData { Mode = PadForge.Services.KeyboardScreenMode.ProfileName }));

            using (var port = new BlissBoxPort(@"\\?\hid#padforge-dialog-binding-test", 0x0D04, Guid.NewGuid(), 1))
            {
                AssertBindsCleanly(() => ScreenDialogTests.Dreamcast(port, null));
                AssertBindsCleanly(() => ScreenDialogTests.Dreamcast(port, new PadForge.Services.BlissBoxPortData { ScreenMode = PadForge.Services.DreamcastScreenMode.ProfilePicture }));
            }

            // The picker is a control in a popup, so a plain window holds it here.
            AssertBindsCleanly(() =>
            {
                var picker = new IconPicker();
                picker.Prepare("", "No Icon");
                return new Window { Content = picker, SizeToContent = SizeToContent.WidthAndHeight };
            });
            AssertBindsCleanly(() =>
            {
                var picker = new IconPicker();
                picker.Prepare("pficon://No Such Package/a.png", "No Icon");
                return new Window { Content = picker, SizeToContent = SizeToContent.WidthAndHeight };
            });
        });

        [Fact]
        public void ChatCommands_BindsCleanly_WithARowInItsList() => CustomTelemetryFormatsDialogTests.RunWithApp(() =>
        {
            var before = ChatCommandRegistry.SaveRegistry();
            try
            {
                Assert.True(ChatCommandRegistry.Add("dialogbindingtest") > 0);
                AssertBindsCleanly(() =>
                {
                    var dialog = new ChatCommandsDialog();
                    Assert.NotEmpty(((ListBox)dialog.FindName("WordListBox")).Items);
                    return dialog;
                });
            }
            finally
            {
                ChatCommandRegistry.LoadRegistry(before);
            }
        });

        /// <summary>The shared confirm in its three forms: a removal, a step
        /// forward with a fact and a word of its own for Cancel, and a
        /// notice.</summary>
        [Theory]
        [InlineData("Delete", ConfirmKind.Destructive, null, null)]
        [InlineData("Apply", ConfirmKind.Primary, "Not Now", "3 axes, 12 buttons")]
        [InlineData(null, ConfirmKind.Destructive, null, null)]
        public void TheConfirm_BindsCleanly(string action, ConfirmKind kind, string cancel, string detail) => CustomTelemetryFormatsDialogTests.RunWithApp(() =>
        {
            var make = typeof(ConfirmDialog).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(string), typeof(string), typeof(string), typeof(string), typeof(ConfirmKind), typeof(string) }, null);
            AssertBindsCleanly(() => (Window)make.Invoke(new object[] { "Delete Rocket League?", "The profile is removed.", action, detail, kind, cancel }));
        });

        [Fact]
        public void ThePlayerNumber_BindsCleanly() => CustomTelemetryFormatsDialogTests.RunWithApp(() =>
        {
            var make = typeof(BlissBoxPlayerDialog).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(int), typeof(string) }, null);
            AssertBindsCleanly(() => (Window)make.Invoke(new object[] { 2, "Bliss-Box 4-Play, port 2" }));
        });

        [Fact]
        public void CopyFrom_BindsCleanly_WithItsRows() => CustomTelemetryFormatsDialogTests.RunWithApp(() =>
        {
            AssertBindsCleanly(() =>
            {
                var dialog = new CopyFromDialog(new[]
                {
                    new CopyFromDialog.DeviceEntry { Name = "Keyboard + Mouse #1", Detail = "Xbox Series X GIP Controller" },
                    new CopyFromDialog.DeviceEntry { Name = "DualShock 3", Detail = "Not assigned, keeps mappings from before" },
                }, "Replace the mappings and per-device tuning of Xbox #1 with another controller's.");
                var list = (ListBox)dialog.FindName("DeviceList");
                Assert.Equal(2, list.Items.Count);
                // The first row is picked on open.
                Assert.Equal(0, list.SelectedIndex);
                return dialog;
            });
        });

        [Fact]
        public void PickASound_BindsCleanly_WithItsRows() => CustomTelemetryFormatsDialogTests.RunWithApp(() =>
        {
            AssertBindsCleanly(() => new PickSoundDialog(new[]
            {
                new PickSoundDialog.Item("alert.wav", "Pack", "pfsound://Pack/alert.wav"),
                new PickSoundDialog.Item("chime.wav", "Pack", "pfsound://Pack/chime.wav"),
            }, allowBrowse: true, previewSlot: 0, previewVolume: 100, preselectValue: "pfsound://Pack/chime.wav"));
        });

        /// <summary>The profile dialog in its three looks, the Edit one with
        /// two games and a polling rate of its own, so its chips and its
        /// More row bind.</summary>
        [Theory]
        [InlineData("saveas")]
        [InlineData("new")]
        [InlineData("edit")]
        public void TheProfileDialog_BindsCleanly(string look) => CustomTelemetryFormatsDialogTests.RunWithApp(() =>
        {
            AssertBindsCleanly(() =>
            {
                var dialog = new ProfileDialog();
                if (look == "new") dialog.ForNew();
                if (look == "edit")
                    dialog.LoadForEdit("Rocket League", new[] { @"C:\Games\RocketLeague.exe", @"D:\Other\Game_2.exe" }, pollingOverrideMs: 2);
                return dialog;
            });
        });

        /// <summary>The Shift Layer window with every row it can show: a
        /// Toggle layer on two inputs with More open, a Cycle layer with
        /// its list, and the Base layer's window.</summary>
        [Theory]
        [InlineData("Toggle")]
        [InlineData("Cycle")]
        [InlineData("Base")]
        public void TheShiftLayer_BindsCleanly(string mode) => CustomTelemetryFormatsDialogTests.RunWithApp(() =>
        {
            const string pad = "7d0a2c5e-0000-0000-0000-000000000001";
            var inputs = new[]
            {
                new PadForge.ViewModels.InputChoice { Descriptor = "Button 4", DisplayName = "Left Bumper", DeviceGuid = pad, DeviceLabel = "Xbox Series X Controller" },
                new PadForge.ViewModels.InputChoice { Descriptor = "Button 5", DisplayName = "Right Bumper", DeviceGuid = pad, DeviceLabel = "Xbox Series X Controller" },
            };
            var others = new[] { new PadForge.Engine.Data.ShiftActivator { LayerName = "Menu", LayerMask = "Menu" } };
            AssertBindsCleanly(() =>
            {
                if (mode == "Base") return new ShiftActivatorDialog("Base", "#7CC7EA", "");
                var layer = new PadForge.Engine.Data.ShiftActivator
                {
                    LayerName = "Sprint", LayerMask = "Sprint", Mode = mode, Kind = mode == "Toggle" ? "Chord" : "Button",
                    Descriptor = "Button 4", DeviceGuid = pad, ChordSecondDescriptor = "Button 5", ChordSecondDeviceGuid = pad,
                    CyclePrevDescriptor = "Button 5", CyclePrevDeviceGuid = pad, CycleLayers = mode == "Cycle" ? "Menu|Sprint" : "",
                    DelayMs = 250, AutoCancelMs = 5000, Color = "#FF6B2C",
                };
                var dialog = new ShiftActivatorDialog(inputs, layer, others);
                Assert.True(((Expander)dialog.FindName("ShiftMore")).IsExpanded);
                return dialog;
            });
        });

        /// <summary>NFC Tags, Voice Macros and Hidden Buttons, each with a
        /// row in its list, and the voice line with a phrase heard.</summary>
        [Fact]
        public void TheRecordBoardsLists_BindCleanly() => CustomTelemetryFormatsDialogTests.RunWithApp(() =>
        {
            var tags = NfcTagRegistry.SaveRegistry();
            var phrases = VoicePhraseRegistry.SaveRegistry();
            var learned = HandheldButtonRegistry.SaveRegistry();
            string machine = HandheldButtonRegistry.MachineKey;
            try
            {
                NfcTagRegistry.LoadRegistry(new[] { ("04A2B91C7F6180", "Mario", 1) });
                VoicePhraseRegistry.LoadRegistry(new[] { ("reload", "Reload", 1) });
                HandheldButtonRegistry.LoadRegistry(new[]
                {
                    new HandheldButtonRegistry.Entry { Name = "Armoury Crate", Button = 0, Keys = new[] { 0x7C } },
                }, machine);
                AssertBindsCleanly(() =>
                {
                    var dialog = new RegisterNfcTagDialog();
                    Assert.Single(((ListBox)dialog.FindName("TagListBox")).Items);
                    return dialog;
                });
                AssertBindsCleanly(() =>
                {
                    var dialog = new RegisterVoicePhraseDialog();
                    Assert.Single(((ListBox)dialog.FindName("PhraseListBox")).Items);
                    ((Expander)dialog.FindName("VoiceMore")).IsExpanded = true;
                    dialog.ShowHeard("Headset Microphone", "reload", 0.93f, fired: true);
                    return dialog;
                });
                AssertBindsCleanly(() =>
                {
                    var dialog = new LearnHandheldButtonDialog();
                    Assert.Single(((ListBox)dialog.FindName("ButtonListBox")).Items);
                    return dialog;
                });
            }
            finally
            {
                HandheldButtonRegistry.LoadRegistry(learned, machine);
                VoicePhraseRegistry.LoadRegistry(phrases);
                NfcTagRegistry.LoadRegistry(tags);
            }
        });

        /// <summary>Import Profile with a binding in each of its lists.</summary>
        [Fact]
        public void TheImportPreview_BindsCleanly() => CustomTelemetryFormatsDialogTests.RunWithApp(() =>
        {
            AssertBindsCleanly(() =>
            {
                var dialog = new ImportConfigDialog(@"C:\Profiles\Star Courier.rewasd", ImportProfileDialogTests.StarCourier());
                Assert.Equal(2, ((ItemsControl)dialog.FindName("NeedsLookList")).Items.Count);
                Assert.Equal(2, ((ItemsControl)dialog.FindName("CleanList")).Items.Count);
                return dialog;
            });
        });

        /// <summary>The Links board's dialogs: the pairing dialog on each
        /// family with its live line showing, Record a Gesture, and the two
        /// Remote Link dialogs.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(4)]
        public void ThePairingDialog_BindsCleanly(int family) => CustomTelemetryFormatsDialogTests.RunWithApp(() =>
        {
            AssertBindsCleanly(() =>
            {
                var settings = new PadForge.ViewModels.SettingsViewModel();
                settings.SerialControllers.Add(new SerialControllerEntry { Port = "TEST", PortName = "COM250", Protocol = "stinger" });
                settings.DjiRemoteHosts.Add("192.168.7.251:40007");
                var dialog = new PairDeviceDialog(settings);
                ((ComboBox)dialog.FindName("FamilyCombo")).SelectedIndex = family;
                typeof(PairDeviceDialog).GetMethod("SetStatus", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(dialog, new object[] { "Working", true, false, false });
                return dialog;
            });
        });

        [Fact]
        public void TheLinksBoardsDialogs_BindCleanly() => CustomTelemetryFormatsDialogTests.RunWithApp(() =>
        {
            AssertBindsCleanly(() => new TouchpadGestureRecorderDialog());
            AssertBindsCleanly(() => new RemoteLinkPairDialog("482915", "A1B2C3D4E5F6071829304A5B6C7D8E9F"));
            AssertBindsCleanly(() => new RemoteLinkPasswordDialog(true, "Prompt"));
            AssertBindsCleanly(() => new RemoteLinkPasswordDialog(false, "Prompt"));
            AssertBindsCleanly(() => new RemoteAssignmentsDialog("Gaming PC", null,
                () => Array.Empty<PadForge.Engine.RemoteLink.RemotePeerDeviceInfo>()));
        });

        /// <summary>The two removal windows with a row of each outcome, as a
        /// report shows them.</summary>
        [Theory]
        [InlineData(typeof(UninstallHIDMaestroDialog))]
        [InlineData(typeof(UninstallBthPS3Dialog))]
        public void ARemovalWindow_BindsCleanly_WithItsRows(Type window) => CustomTelemetryFormatsDialogTests.RunWithApp(() =>
        {
            AssertBindsCleanly(() =>
            {
                var make = window.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)[0];
                var dialog = (Window)make.Invoke(new object[] { null });
                var rowType = window.GetNestedType("EntryRow");
                var rows = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(rowType));
                foreach (var (title, outcome, removed, detail) in new[]
                         {
                             ("Driver package", "Removed", true, (string)null),
                             ("Service setting", "Kept", false, "Stays: HIDMaestro never recorded its earlier value"),
                         })
                {
                    object row = Activator.CreateInstance(rowType);
                    rowType.GetProperty("Kind").SetValue(row, title);
                    rowType.GetProperty("Title").SetValue(row, title);
                    rowType.GetProperty("Location").SetValue(row, "oem5.inf");
                    rowType.GetProperty("Outcome").SetValue(row, outcome);
                    rowType.GetProperty("IsRemoved").SetValue(row, removed);
                    rowType.GetProperty("Detail").SetValue(row, detail);
                    rows.Add(row);
                }
                window.GetMethod("ShowEntries", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(dialog, new object[] { rows });
                Assert.Equal(2, ((ListBox)dialog.FindName("EntryList")).Items.Count);
                return dialog;
            });
        });

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void TheKeepPrompt_BindsCleanly(bool mouse, bool touchpad) => CustomTelemetryFormatsDialogTests.RunWithApp(() =>
        {
            var make = typeof(KeepDriverDialog).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(string), typeof(int), typeof(bool), typeof(bool) }, null);
            AssertBindsCleanly(() => (Window)make.Invoke(new object[] { "Keyboard", 15, mouse, touchpad }));
        });
    }
}
