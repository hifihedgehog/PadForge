using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using PadForge.Common.Input;
using PadForge.Controls;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;
using InputManager = PadForge.Common.Input.InputManager;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// Add a Controller (#574), the dialog drawn over the page area that took
    /// the Add Controller popup's place: its seven tiles keep the popup
    /// buttons' ids, names, tooltips and rules, Driven By offers the connected
    /// game controllers no controller uses, and the window opens it from every
    /// way in. The in-window dialog it stands in is ShellDialog, which Search
    /// shares.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AddControllerTests : IDisposable
    {
        private readonly SettingsCollection _savedSettings = SettingsManager.UserSettings;
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
        }

        private static int At(string text, string needle)
        {
            int at = text.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(at >= 0, needle + " is not there");
            Assert.True(at == text.LastIndexOf(needle, StringComparison.Ordinal), needle + " is there twice");
            return at;
        }

        private static string Popup(VirtualControllerType type) => type switch
        {
            VirtualControllerType.PlayStation => "AddDS4Btn",
            VirtualControllerType.Nintendo => "AddNintendoBtn",
            VirtualControllerType.Extended => "AddRawBtn",
            VirtualControllerType.KeyboardMouse => "AddKeyboardMouseBtn",
            VirtualControllerType.Midi => "AddMidiBtn",
            VirtualControllerType.Vr => "AddVrBtn",
            _ => "AddXbox360Btn",
        };

        /// <summary>Each type is a tile with the popup button's id, name and
        /// tooltip, its own line, and the icon every type chip draws.</summary>
        [Fact]
        public void EachType_IsATileWithThePopupsIdNameAndTooltip()
        {
            var s = Strings.Instance;
            var names = new System.Collections.Generic.Dictionary<VirtualControllerType, (string Name, string Line)>
            {
                [VirtualControllerType.Xbox] = (s.ControllerType_Xbox, s.AddController_Xbox_Line),
                [VirtualControllerType.PlayStation] = (s.ControllerType_PlayStation, s.AddController_PlayStation_Line),
                [VirtualControllerType.Nintendo] = (s.ControllerType_Nintendo, s.AddController_Nintendo_Line),
                [VirtualControllerType.Extended] = (s.ControllerType_Extended, s.AddController_Extended_Line),
                [VirtualControllerType.KeyboardMouse] = (s.ControllerType_KeyboardMouse, s.AddController_Kbm_Line),
                [VirtualControllerType.Midi] = (s.ControllerType_MIDI, s.AddController_Midi_Line),
                [VirtualControllerType.Vr] = (s.ControllerType_VR, s.AddController_Vr_Line),
            };
            foreach (VirtualControllerType type in Enum.GetValues(typeof(VirtualControllerType)))
            {
                var choice = ControllerTypeChoice.For(type, 0, 0, true, true);
                Assert.Equal(Popup(type), choice.AutomationId);
                Assert.Equal(names[type].Name, choice.Name);
                Assert.Equal(names[type].Name, choice.ToolTip);
                Assert.Equal(names[type].Line, choice.Line);
                Assert.True(choice.IsAvailable);
                Assert.Same(SlotTypeIconMap.GeometryFor(type), choice.Geometry);
                Assert.Equal(SlotTypeIconMap.GlyphFor(type), choice.Glyph);
            }
        }

        /// <summary>Common holds Xbox, PlayStation and Nintendo, More Outputs
        /// the other four, as the board draws them: every type once.</summary>
        [Fact]
        public void TheTiles_RunInTheBoardsOrder()
        {
            var vm = AddControllerViewModel.Build(null, _ => 0, 0, true, true);
            Assert.Equal(new[] { VirtualControllerType.Xbox, VirtualControllerType.PlayStation, VirtualControllerType.Nintendo },
                vm.Common.Select(c => c.Type).ToArray());
            Assert.Equal(new[] { VirtualControllerType.KeyboardMouse, VirtualControllerType.Extended, VirtualControllerType.Midi, VirtualControllerType.Vr },
                vm.More.Select(c => c.Type).ToArray());
            Assert.Equal(Enum.GetValues(typeof(VirtualControllerType)).Length, vm.Common.Count + vm.More.Count);
            Assert.False(vm.IsFull);
        }

        /// <summary>A type that cannot be made keeps its tile and says why: the
        /// popup's tooltip, word for word, and a line in plain words.</summary>
        [Theory]
        [InlineData("midi-missing")]
        [InlineData("vr-missing")]
        [InlineData("vr-one")]
        [InlineData("vr-both")]
        public void ATypeThatCannotBeMade_SaysWhy(string state)
        {
            var s = Strings.Instance;
            ControllerTypeChoice choice;
            string tip, line;
            switch (state)
            {
                case "midi-missing":
                    choice = ControllerTypeChoice.For(VirtualControllerType.Midi, 0, 0, false, true);
                    tip = s.Main_MIDI_NotRunning;
                    line = s.AddController_MidiNotRunning;
                    break;
                case "vr-missing":
                    choice = ControllerTypeChoice.For(VirtualControllerType.Vr, 0, 0, true, false);
                    tip = s.Main_VR_RequiresSteamVR;
                    line = s.AddController_VrNeedsSteamVr;
                    break;
                case "vr-one":
                    choice = ControllerTypeChoice.For(VirtualControllerType.Vr, 1, 1, true, true);
                    tip = string.Format(s.Main_VR_Max_Format, SettingsManager.MaxVrSlots);
                    line = s.AddController_VrOnlyOne;
                    break;
                default:
                    // SteamVR's reason comes first, as it did in the popup.
                    choice = ControllerTypeChoice.For(VirtualControllerType.Vr, 1, 1, true, false);
                    tip = s.Main_VR_RequiresSteamVR;
                    line = s.AddController_VrNeedsSteamVr;
                    break;
            }
            Assert.False(choice.IsAvailable);
            Assert.Equal(tip, choice.ToolTip);
            Assert.Equal(line, choice.Line);
        }

        /// <summary>At sixteen controllers no tile can be used, each keeps its
        /// description, and one line under the subtitle says why.</summary>
        [Fact]
        public void SixteenControllers_LeaveNoTileAndSayWhy()
        {
            var vm = AddControllerViewModel.Build(null, t => t == VirtualControllerType.Xbox ? InputManager.MaxPads : 0,
                InputManager.MaxPads, true, true);
            foreach (var tile in vm.Common.Concat(vm.More))
            {
                Assert.False(tile.IsAvailable);
                Assert.Equal(ControllerTypeChoice.LineOf(tile.Type), tile.Line);
            }
            Assert.True(vm.IsFull);
            Assert.Equal(string.Format(Strings.Instance.AddController_Full_Format, 16), vm.FullNotice);
        }

        /// <summary>Driven By lists the devices given, the first checked and
        /// taken, then Choose Later. With none, the block is not shown and the
        /// controller takes no device.</summary>
        [Fact]
        public void DrivenBy_ListsItsDevicesThenChooseLater()
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var one = AddControllerViewModel.Build(new[] { DrivenByChoice.For(a, "Pad A", false, null, 0) }, _ => 0, 0, true, true);
            Assert.True(one.ShowsDrivenBy);
            Assert.Equal(2, one.Devices.Count);
            Assert.True(one.Devices[0].IsChecked);
            Assert.False(one.Devices[1].IsChecked);
            Assert.Equal(a, one.SelectedDevice);
            Assert.Equal(Guid.Empty, one.Devices[1].Device);
            Assert.Equal(new[] { "AddController_Device_0", "AddController_ChooseLater" }, one.Devices.Select(d => d.AutomationId).ToArray());

            var two = AddControllerViewModel.Build(new[]
            {
                DrivenByChoice.For(a, "Pad A", false, null, 0, "\uE856"),
                DrivenByChoice.For(b, "Pad B", true, "64%", 1, "\uE856"),
            }, _ => 0, 0, true, true);
            Assert.Equal(3, two.Devices.Count);
            Assert.Equal(new[] { true, false, false }, two.Devices.Select(d => d.IsChecked).ToArray());
            Assert.Equal(a, two.SelectedDevice);
            Assert.Equal(new[] { "AddController_Device_0", "AddController_Device_1", "AddController_ChooseLater" },
                two.Devices.Select(d => d.AutomationId).ToArray());
            Assert.Equal(" Bluetooth, ", two.Devices[1].LineLead);
            Assert.Equal("64%", two.Devices[1].LineBattery);
            // The battery's picture stands before its level, and a device
            // with no battery has none whatever it is handed.
            Assert.Equal("\uE856", two.Devices[1].BatteryGlyph);
            Assert.Equal(" ", two.Devices[1].BatteryGap);
            Assert.Equal(string.Empty, two.Devices[0].BatteryGlyph);
            Assert.Equal(string.Empty, two.Devices[0].BatteryGap);
            Assert.Equal("Pad B, Bluetooth, 64%", two.Devices[1].AccessibleName);
            Assert.Equal("Pad A", two.Devices[0].AccessibleName);

            var none = AddControllerViewModel.Build(Array.Empty<DrivenByChoice>(), _ => 0, 0, true, true);
            Assert.False(none.ShowsDrivenBy);
            Assert.Equal(Guid.Empty, none.SelectedDevice);
        }

        /// <summary>The words after a controller's name: Bluetooth and the
        /// battery by the format given, Bluetooth alone, the battery alone,
        /// or nothing. A format with no place for the battery is all
        /// lead.</summary>
        [Theory]
        [InlineData(true, "64%", "Bluetooth, {0}", "{0}", "Bluetooth, ", "64%", "")]
        [InlineData(true, null, "Bluetooth, {0}", "{0}", "BT", "", "")]
        [InlineData(false, "64%", "Bluetooth, {0}", "{0}", "", "64%", "")]
        [InlineData(false, "82%", "Bluetooth, battery {0}", "Battery {0}", "Battery ", "82%", "")]
        [InlineData(false, null, "Bluetooth, {0}", "{0}", "", "", "")]
        [InlineData(true, "64%", "Bluetooth only", "{0}", "Bluetooth only", "", "")]
        public void TheDeviceLine_ReadsBluetoothAndTheBattery(bool bluetooth, string battery, string both, string alone,
            string lead, string level, string tail)
        {
            if (lead == "BT") lead = Strings.Instance.Pad_Transport_Bluetooth;
            Assert.Equal((lead, level, tail), DeviceLine.Parts(bluetooth, battery, both, alone));
        }

        /// <summary>A device Add a Controller can offer is connected, a game
        /// controller by Home offer's test, and on no virtual controller.</summary>
        [Theory]
        [InlineData(true, InputDeviceType.Gamepad, false, true)]
        [InlineData(false, InputDeviceType.Gamepad, false, false)]
        [InlineData(true, InputDeviceType.Keyboard, false, false)]
        [InlineData(true, InputDeviceType.Gamepad, true, false)]
        public void AnUnusedGameController_IsConnectedAControllerAndOnNoSlot(bool online, int capType, bool assigned, bool expected)
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var ud = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                ProductName = "Pad",
                InstanceName = "Pad",
                CapType = capType,
                IsOnline = online,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            if (assigned)
                lock (SettingsManager.UserSettings.SyncRoot)
                    SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = ud.InstanceGuid, MapTo = 0 });
            Assert.Equal(expected, InputService.IsUnusedGameController(ud));
            Assert.False(InputService.IsUnusedGameController(null));
        }

        /// <summary>Every way in opens the dialog: the sidebar's entry, Home's
        /// tile, Use It on Home's offer for its device, and the first page's
        /// link for the device it names. The sidebar goes dark while it is
        /// open, and the type chips keep their bar.</summary>
        [Fact]
        public void TheWindow_OpensTheDialogFromEveryWayIn()
        {
            string code = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            int activate = At(code, "private void ActivateNavItem(NavigationViewItem nvi)");
            int entry = At(code, "OpenAddController(Guid.Empty, nvi);");
            Assert.True(entry > activate && entry - activate < 300, "the sidebar's entry does not open the dialog");
            At(code, "OpenAddController(Guid.Empty, HomePageView.AddControllerCardElement);");
            // Home's offer and the Devices page's Use It and New Controller
            // both pass the device and what was clicked.
            Assert.Matches(@"HomePageView\.HomeOfferUseRequested \+= \(s, args\) => OpenAddController\(args\.Device, args\.Anchor\);", code);
            Assert.Matches(@"DevicesPageView\.NewControllerRequested \+= \(s, args\) => OpenAddController\(args\.Device, args\.Anchor\);", code);
            Assert.Equal(2, Regex.Matches(code, @"OpenAddController\(args\.Device, args\.Anchor\);").Count);
            At(code, "OpenAddController(_viewModel.Dashboard.FirstRunDeviceGuid, opener);");
            int open = At(code, "internal void OpenAddController(Guid device, IInputElement opener)");
            int dark = code.IndexOf("SetNavActiveByTag(null);", open, StringComparison.Ordinal);
            Assert.True(dark > open && dark - open < 600, "the sidebar stays lit under the dialog");
            // The two type chips, Home's and the sidebar's, open the type bar.
            At(code, "private void ShowTypeBar(UIElement anchor, PlacementMode placement, int slot, UIElement opener)");
            Assert.Equal(2, Regex.Matches(code, @"(?<!void )\bShowTypeBar\(").Count);
            // A change of page closes it, and only a change.
            int navigate = At(code, "private void NavigateToTag(string tag)");
            int change = code.IndexOf("if (tag != _viewModel.SelectedNavTag)", navigate, StringComparison.Ordinal);
            int closes = code.IndexOf("CloseAddController(returnFocus: false, restoreNav: false);", change, StringComparison.Ordinal);
            Assert.True(change > navigate && closes > change && closes - change < 120, "a change of page does not close the dialog");
            // Escape and the X give the focus back to what opened it.
            At(code, "AddControllerHost.CloseRequested += (s, e) => CloseAddController(returnFocus: true, restoreNav: true);");
            At(code, "AddControllerView.CloseRequested += (s, e) => CloseAddController(returnFocus: true, restoreNav: true);");
        }

        /// <summary>The dialog as its board draws it, top to bottom, in the
        /// page area after the pages.</summary>
        [Fact]
        public void TheDialog_IsDrawnAsItsBoard()
        {
            string window = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml");
            int about = At(window, "<views:AboutPage x:Name=\"AboutPageView\"");
            int host = At(window, "<controls:ShellDialog x:Name=\"AddControllerHost\" Panel.ZIndex=\"5\" FrameMaxWidth=\"760\"");
            Assert.True(host > about, "the dialog does not stand after the pages");
            At(window, "AutomationProperties.AutomationId=\"AddControllerDialog\"");
            At(window, "<views:AddControllerView x:Name=\"AddControllerView\"/>");

            string view = ExtrasPageTests.RepoText("PadForge.App", "Views", "AddControllerView.xaml");
            int title = At(view, "<TextBlock x:Name=\"AddControllerTitle\" AutomationProperties.HeadingLevel=\"Level1\"");
            Assert.Contains("FontSize=\"20\" FontWeight=\"SemiBold\"", view.Substring(title, 300));
            int subtitle = At(view, "Text=\"{Binding AddController_Subtitle, Source={x:Static strings:Strings.Instance}}\"");
            int close = At(view, "AutomationProperties.AutomationId=\"AddController_Close\"");
            int drivenBy = At(view, "Text=\"{Binding AddController_DrivenBy, Source={x:Static strings:Strings.Instance}}\"");
            int common = At(view, "<controls:TileGridPanel MinItemWidth=\"150\" Gap=\"12\" MaxColumns=\"3\"/>");
            int more = At(view, "<controls:TileGridPanel MinItemWidth=\"120\" Gap=\"12\" MaxColumns=\"4\"/>");
            int foot = At(view, "<TextBlock Margin=\"24,18,24,22\" FontSize=\"13\"");
            Assert.True(title < subtitle && subtitle < close && close < drivenBy && drivenBy < common && common < more && more < foot,
                "the dialog's parts are out of the board's order");
            string closeButton = Regex.Match(view, "<ui:Button x:Name=\"CloseButton\".*?</ui:Button>", RegexOptions.Singleline).Value;
            Assert.Contains("Style=\"{StaticResource QuietButton}\"", closeButton);
            Assert.Contains("Width=\"32\" Height=\"32\"", closeButton);
            Assert.Contains("Data=\"{StaticResource CloseLineGeometry}\" Size=\"16\" StrokeWidth=\"1.8\"", closeButton);
            Assert.Contains("FontSize=\"14\"", view.Substring(subtitle, close - subtitle));
            // The tiles keep the popup's AvailabilityButton.
            Assert.Equal(2, Regex.Matches(view, "<controls:AvailabilityButton Style=\"\\{StaticResource TypeTileButton\\}\"").Count);
        }

        /// <summary>The shipped ShellDialog style, read from the markup: the
        /// dialog shows while open, a screen reader hears a pane that is a
        /// dialog, Tab cycles inside it, Escape anywhere in the window asks to
        /// close it, and closing gives the focus back to its opener.</summary>
        [Fact]
        public void ShellDialog_IsADialog_ClosesOnEscape_AndCyclesTab()
        {
            string icons = ExtrasPageTests.RepoText("PadForge.App", "Resources", "ControllerIcons.xaml");
            var m = Regex.Match(icons, "<Style TargetType=\"\\{x:Type controls:ShellDialog\\}\">.*?\\n    </Style>", RegexOptions.Singleline);
            Assert.True(m.Success, "ControllerIcons.xaml has no ShellDialog style");
            string styleText = m.Value.Replace("<Style TargetType=",
                "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
                + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" "
                + "xmlns:controls=\"clr-namespace:PadForge.Controls;assembly=PadForge\" TargetType=");

            Exception failure = null;
            var thread = new Thread(() =>
            {
                Window window = null;
                try
                {
                    var style = (Style)XamlReader.Parse(styleText);
                    var opener = new Button { Content = "Open" };
                    var inside = new Button { Content = "Inside" };
                    var dialog = new ShellDialog { Style = style, Content = inside };
                    var root = new Grid();
                    root.Children.Add(opener);
                    root.Children.Add(dialog);
                    window = new Window
                    {
                        Content = root, Width = 400, Height = 300, Left = -10000, Top = -10000,
                        ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                    };
                    window.Show();
                    window.UpdateLayout();

                    Assert.False(dialog.IsOpen);
                    Assert.Equal(Visibility.Collapsed, dialog.Visibility);
                    opener.Focus();
                    dialog.Open(opener);
                    Assert.True(dialog.IsOpen);
                    Assert.Equal(Visibility.Visible, dialog.Visibility);
                    Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(dialog));

                    var peer = UIElementAutomationPeer.CreatePeerForElement(dialog);
                    Assert.True(peer.IsDialog());
                    Assert.Equal(AutomationControlType.Pane, peer.GetAutomationControlType());

                    int asked = 0;
                    dialog.CloseRequested += (s, e) => asked++;
                    var source = PresentationSource.FromVisual(window);
                    Assert.NotNull(source);
                    var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    inside.RaiseEvent(escape);
                    Assert.Equal(1, asked);
                    Assert.True(escape.Handled);
                    // Another key passes through.
                    var other = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.A) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    inside.RaiseEvent(other);
                    Assert.Equal(1, asked);
                    Assert.False(other.Handled);

                    inside.Focus();
                    dialog.Close(true);
                    Assert.False(dialog.IsOpen);
                    Assert.Equal(Visibility.Collapsed, dialog.Visibility);
                    Assert.True(opener.IsKeyboardFocused || FocusManager.GetFocusedElement(window) == opener,
                        "the focus did not go back to the opener");

                    // Closed, Escape is the window's again.
                    var after = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    opener.RaiseEvent(after);
                    Assert.Equal(1, asked);
                }
                catch (Exception error) { failure = error; }
                finally { window?.Close(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the dialog run timed out");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
