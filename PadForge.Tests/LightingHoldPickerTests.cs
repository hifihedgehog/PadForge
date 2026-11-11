using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Controls;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Lighting Hold row on the Pad page's Lighting tab (#494): which
    /// devices show it, which of its options a device is offered, and the
    /// picker and its reset as the page declares them, loaded from the
    /// page's own markup and bound the way the page binds them.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class LightingHoldPickerTests : IDisposable
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-0000000000a1");
        private static readonly Guid B = new("00000000-0000-0000-0000-0000000000b2");

        public LightingHoldPickerTests() => PeripheralOutputs.ResetForTest();

        public void Dispose() => PeripheralOutputs.ResetForTest();

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        // ─────────────────────────────────────────────
        //  Which holds a device offers
        // ─────────────────────────────────────────────

        /// <summary>A device offers a hold when its record or its links say
        /// so: the record keeps the row on the tab while the device sleeps
        /// or is unplugged, and the links show it the moment a path of the
        /// device's own lights it. The second hold is offered only on top
        /// of the first. A path with no hold to offer, a zone-only device
        /// or one lit through its vendor's program, shows no row.</summary>
        [Theory]
        // What the record holds, the highest hold the links carry (-1 for a
        // device with no links now), and what the tab offers.
        [InlineData(0, -1, 0)]
        [InlineData(2, -1, 0)]
        [InlineData(6, -1, 1)]
        [InlineData(14, -1, 2)]
        [InlineData(2, 0, 0)]
        [InlineData(2, 1, 1)]
        [InlineData(2, 2, 2)]
        [InlineData(6, 2, 2)]
        [InlineData(14, 1, 2)]
        // The second hold's bit with no first on record offers nothing.
        [InlineData(10, -1, 0)]
        public void ADeviceOffersAHold_ByItsRecordOrItsLinks(int recorded, int linked, int offered)
        {
            var keyboard = new UserDevice
            {
                InstanceGuid = A, IsOnline = true, CapType = InputDeviceType.Keyboard,
                VendorId = PeripheralLinker.LogitechVid, PeripheralOutputs = recorded,
            };
            // Another device's links never count for this one.
            var other = new DeviceLinks
            {
                Device = B, Lighting = new[] { new OutputPath(OutputFamily.HidppUnit, "rx|02") }, MaxClaimLevel = 2,
            };
            if (linked < 0)
                PeripheralOutputs.PublishLinks(new LinkTable(new[] { other }));
            else
                PeripheralOutputs.PublishLinks(new LinkTable(new[]
                {
                    other,
                    new DeviceLinks
                    {
                        Device = A, Lighting = new[] { new OutputPath(OutputFamily.HidppUnit, "rx|01") }, MaxClaimLevel = linked,
                    },
                }));

            Assert.Equal(offered, PadPage.OfferedLightingHold(keyboard));
            Assert.Equal(0, PadPage.OfferedLightingHold(null));
        }

        /// <summary>The record's two bits are the ones the links write: a
        /// row whose links carry a hold records it, and the record alone
        /// then offers what the links did.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void WhatTheLinksOffer_TheRecordOffersWhileTheDeviceSleeps(int level)
        {
            var links = new DeviceLinks
            {
                Device = A, Lighting = new[] { new OutputPath(OutputFamily.HidppUnit, "rx|01") }, MaxClaimLevel = level,
            };
            var asleep = new UserDevice { InstanceGuid = A, PeripheralOutputs = PeripheralLinker.Capabilities(0, links, stillLooking: false) };
            Assert.True(asleep.HasPeripheralLighting);
            Assert.Equal(level, PadPage.OfferedLightingHold(asleep));
        }

        // ─────────────────────────────────────────────
        //  The row as the page declares it
        // ─────────────────────────────────────────────

        /// <summary>What the row binds against: the tab's data context holds
        /// the selected device's config, and swaps it when another device
        /// is selected.</summary>
        private sealed class Tab : INotifyPropertyChanged
        {
            private DeviceSlotConfig _deviceConfig = new();

            public DeviceSlotConfig DeviceConfig
            {
                get => _deviceConfig;
                set
                {
                    _deviceConfig = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DeviceConfig)));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        private sealed record LoadedRow(Grid Root, SettingRow Row, TextBlock Label, ComboBox Picker,
            ComboBoxItem HostModeItem, SettingResetButton Reset);

        /// <summary>The Lighting Hold row, read out of PadPage.xaml and
        /// loaded with the two resources it takes from the page and the
        /// app: the label style and the enum converter.</summary>
        private static LoadedRow LoadRow()
        {
            string page = RepoText("PadForge.App", "Views", "PadPage.xaml");
            const string open = "<controls:SettingRow Grow=\"True\" ValueWidth=\"66\" MinHeight=\"44\" x:Name=\"PeripheralClaimLevelRow\"";
            const string close = "</controls:SettingRow>";
            int start = page.IndexOf(open, StringComparison.Ordinal);
            Assert.True(start > 0, "the Lighting Hold row is gone from the page");
            int end = page.IndexOf(close, start, StringComparison.Ordinal);
            Assert.True(end > start);
            string row = page.Substring(start, end + close.Length - start);

            string xaml =
                "<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\""
                + " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\""
                + " xmlns:strings=\"clr-namespace:PadForge.Resources.Strings;assembly=PadForge\""
                + " xmlns:reset=\"clr-namespace:PadForge.Controls;assembly=PadForge\""
                + " xmlns:controls=\"clr-namespace:PadForge.Controls;assembly=PadForge\""
                + " xmlns:conv=\"clr-namespace:PadForge.Converters;assembly=PadForge\">"
                + "<Grid.Resources>"
                + "<conv:EnumIndexConverter x:Key=\"EnumIndexConverter\"/>"
                + "<Style x:Key=\"DzLabel\" TargetType=\"TextBlock\"/>"
                + "</Grid.Resources>"
                + row
                + "</Grid>";
            var root = (Grid)XamlReader.Parse(xaml);
            var panel = Assert.IsType<SettingRow>(root.FindName("PeripheralClaimLevelRow"));
            var picker = Assert.IsType<ComboBox>(root.FindName("PeripheralClaimLevelPicker"));
            var hostMode = Assert.IsType<ComboBoxItem>(root.FindName("PeripheralClaimLevelHostModeItem"));
            Assert.Equal(3, panel.Children.Count);
            Assert.Same(picker, panel.Children[1]);
            return new LoadedRow(root, panel,
                Assert.IsType<TextBlock>(panel.Children[0]),
                picker,
                hostMode,
                Assert.IsType<SettingResetButton>(panel.Children[2]));
        }

        /// <summary>Lets the bindings a change queued run.</summary>
        private static void Settle()
            => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        /// <summary>WPF elements need an STA thread.</summary>
        private static void RunSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            Assert.True(thread.Join(30000), "the Lighting Hold row test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>The row stays off the tab until a device offers a hold,
        /// and its last option is in the list only for a device that offers
        /// that one. Out of the list means collapsed and disabled: a closed
        /// picker's arrow keys step to the next option that is enabled,
        /// collapsed or not, and typing a letter is switched off for the
        /// picker because it would select the option either way. The
        /// tooltip follows the list: with the last option out of it, the
        /// picker shows the text that stops before the sentences about
        /// that option.</summary>
        [Fact]
        public void TheRowShowsForADeviceThatOffersAHold_AndItsLastOptionOnlyWhereThatOneIsOffered() => RunSta(() =>
        {
            var s = Strings.Instance;
            var row = LoadRow();
            var colors = (ComboBoxItem)row.Picker.Items[0];
            var power = (ComboBoxItem)row.Picker.Items[1];
            Assert.Same(row.HostModeItem, row.Picker.Items[2]);
            Settle();
            // As declared, before the first look at a device.
            Assert.Equal(Visibility.Collapsed, row.Row.Visibility);
            Assert.False(row.Picker.IsTextSearchEnabled);
            Assert.Equal(s.Pad_Lighting_ClaimLevel_Tooltip, row.Picker.ToolTip);
            Assert.NotEqual(s.Pad_Lighting_ClaimLevel_Tooltip, s.Pad_Lighting_ClaimLevel_TooltipNoHostMode);

            void Offer(int offered, bool shown, bool lastOption)
            {
                PadPage.ShowLightingHold(row.Row, row.Picker, row.HostModeItem, offered);
                Settle();
                Assert.Equal(shown ? Visibility.Visible : Visibility.Collapsed, row.Row.Visibility);
                Assert.Equal(lastOption ? Visibility.Visible : Visibility.Collapsed, row.HostModeItem.Visibility);
                Assert.Equal(lastOption, row.HostModeItem.IsEnabled);
                // The first two options are never taken away.
                Assert.True(colors.IsEnabled && power.IsEnabled);
                Assert.Equal((Visibility.Visible, Visibility.Visible), (colors.Visibility, power.Visibility));
                Assert.Equal(lastOption ? s.Pad_Lighting_ClaimLevel_Tooltip : s.Pad_Lighting_ClaimLevel_TooltipNoHostMode,
                    row.Picker.ToolTip);
            }

            Offer(0, shown: false, lastOption: false);
            Offer(1, shown: true, lastOption: false);
            Offer(2, shown: true, lastOption: true);
            // Another device is selected, one that offers less.
            Offer(1, shown: true, lastOption: false);
            Offer(0, shown: false, lastOption: false);

            // The tooltip is bound, so it follows a change of language, and
            // a look that changes nothing leaves the binding in place.
            var bound = System.Windows.Data.BindingOperations.GetBindingExpression(row.Picker, FrameworkElement.ToolTipProperty);
            Assert.NotNull(bound);
            Assert.Same(s, bound.ParentBinding.Source);
            PadPage.ShowLightingHold(row.Row, row.Picker, row.HostModeItem, 0);
            Assert.Same(bound, System.Windows.Data.BindingOperations.GetBindingExpression(row.Picker, FrameworkElement.ToolTipProperty));

            // The page's fields are null until its markup has loaded.
            PadPage.ShowLightingHold(null, null, null, 2);
        });

        /// <summary>A keyboard with no key held, so a key press handed to
        /// the picker reads the same whatever is held on the bench's own
        /// keyboard: the picker asks the press's device for Alt
        /// (dotnet/wpf, PresentationFramework, ComboBox.cs:1325-1465), and
        /// the device answers from here (PresentationCore,
        /// KeyboardDevice.cs:174-215).</summary>
        private sealed class NoKeysHeld : KeyboardDevice
        {
            public NoKeysHeld() : base(System.Windows.Input.InputManager.Current) { }

            protected override KeyStates GetKeyStatesFromSystem(Key key) => KeyStates.None;
        }

        /// <summary>The last option, out of the list, is out of the
        /// keyboard's reach on the closed picker: the arrow keys and End
        /// stop at the option before it, and a typed letter selects
        /// nothing. The same presses reach it where it is offered, and the
        /// same letters select it once type-ahead is switched back on,
        /// collapsed and disabled as it is, which is why the page switches
        /// type-ahead off. The letters are typed with no message pumped
        /// between them, so the type-ahead's timeout, a dispatcher timer
        /// (TextSearch.cs:527-543), never runs inside the burst.</summary>
        [Fact]
        public void TheLastOption_OutOfTheList_IsOutOfTheKeyboardsReach() => RunSta(() =>
        {
            var row = LoadRow();
            var tab = new Tab();
            row.Root.DataContext = tab;
            var taken = new List<PeripheralClaimLevel>();
            tab.DeviceConfig.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DeviceSlotConfig.PeripheralClaimLevel)) taken.Add(tab.DeviceConfig.PeripheralClaimLevel);
            };
            var window = new Window
            {
                Content = row.Root, Width = 900, Height = 200, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000,
            };
            try
            {
                window.Show();
                Settle();
                var source = PresentationSource.FromVisual(row.Picker);
                Assert.NotNull(source);
                var keyboard = new NoKeysHeld();

                int Press(Key key, int from)
                {
                    row.Picker.SelectedIndex = from;
                    row.Picker.RaiseEvent(new KeyEventArgs(keyboard, source, 0, key) { RoutedEvent = UIElement.KeyDownEvent });
                    return row.Picker.SelectedIndex;
                }

                // The letter the last option starts with, three times in a
                // row. With type-ahead on, a repeated letter walks the
                // options that start with it, and in every language the
                // last two do.
                string letter = ((string)row.HostModeItem.Content).Substring(0, 1);
                List<int> Type(int from)
                {
                    row.Picker.SelectedIndex = from;
                    var selected = new List<int>();
                    for (int i = 0; i < 3; i++)
                    {
                        row.Picker.RaiseEvent(new TextCompositionEventArgs(keyboard,
                            new TextComposition(System.Windows.Input.InputManager.Current, row.Picker, letter))
                        {
                            RoutedEvent = UIElement.TextInputEvent,
                        });
                        selected.Add(row.Picker.SelectedIndex);
                    }
                    return selected;
                }

                // Offered: the keys reach it. The control for every press
                // below, on the same picker.
                PadPage.ShowLightingHold(row.Row, row.Picker, row.HostModeItem, 2);
                Assert.Equal(2, Press(Key.Down, from: 1));
                Assert.Equal(2, Press(Key.Right, from: 1));
                Assert.Equal(2, Press(Key.End, from: 0));
                Assert.Contains(PeripheralClaimLevel.HostMode, taken);

                // Out of the list.
                PadPage.ShowLightingHold(row.Row, row.Picker, row.HostModeItem, 1);
                row.Picker.SelectedIndex = 0;
                taken.Clear();
                Assert.Equal(1, Press(Key.Down, from: 1));
                Assert.Equal(1, Press(Key.Right, from: 1));
                Assert.Equal(1, Press(Key.End, from: 0));
                Assert.Equal(1, Press(Key.Down, from: 0));
                Assert.DoesNotContain(2, Type(from: 0));
                Assert.DoesNotContain(2, Type(from: 1));
                Assert.DoesNotContain(PeripheralClaimLevel.HostMode, taken);

                // Why type-ahead is off for this picker.
                Assert.False(row.Picker.IsTextSearchEnabled);
                row.Picker.IsTextSearchEnabled = true;
                Assert.Contains(2, Type(from: 0));
                Assert.Contains(PeripheralClaimLevel.HostMode, taken);
            }
            finally { window.Close(); }
        });

        /// <summary>The picker shows the selected device's hold and writes a
        /// pick back to it, option for option in the enum's order, the
        /// reset beside it returns the hold to colors alone, and selecting
        /// another device shows that device's hold without writing to
        /// either. A hold saved earlier that the device no longer offers
        /// stays saved and stays shown.</summary>
        [Fact]
        public void ThePickerReadsAndWritesTheSelectedDevicesHold_AsThePageDeclaresIt() => RunSta(() =>
        {
            var s = Strings.Instance;
            var row = LoadRow();
            var tab = new Tab();
            var first = tab.DeviceConfig;
            row.Root.DataContext = tab;
            Settle();

            // Each option is its hold, in the enum's order.
            string[] names = { s.Pad_Lighting_ClaimLevel_Colors, s.Pad_Lighting_ClaimLevel_Power, s.Pad_Lighting_ClaimLevel_HostMode };
            var holds = Enum.GetValues<PeripheralClaimLevel>();
            Assert.Equal(holds.Length, row.Picker.Items.Count);
            Assert.Equal(names, row.Picker.Items.Cast<ComboBoxItem>().Select(item => (string)item.Content));
            Assert.Equal(0, row.Picker.SelectedIndex);
            foreach (var hold in holds.Reverse())
            {
                first.PeripheralClaimLevel = hold;
                Assert.Equal((int)hold, row.Picker.SelectedIndex);
            }
            foreach (var hold in holds.Reverse())
            {
                row.Picker.SelectedIndex = (int)hold;
                Assert.Equal(hold, first.PeripheralClaimLevel);
            }

            // The reset beside the picker.
            first.PeripheralClaimLevel = PeripheralClaimLevel.HostMode;
            Assert.NotNull(row.Reset.Command);
            row.Reset.Command.Execute(row.Reset.CommandParameter);
            Assert.Equal(PeripheralClaimLevel.Colors, first.PeripheralClaimLevel);
            Assert.Equal(0, row.Picker.SelectedIndex);

            // Another device is selected. Each keeps its own hold.
            first.PeripheralClaimLevel = PeripheralClaimLevel.HostMode;
            var second = new DeviceSlotConfig { PeripheralClaimLevel = PeripheralClaimLevel.Power };
            tab.DeviceConfig = second;
            Settle();
            Assert.Equal(1, row.Picker.SelectedIndex);
            Assert.Equal(PeripheralClaimLevel.HostMode, first.PeripheralClaimLevel);
            Assert.Equal(PeripheralClaimLevel.Power, second.PeripheralClaimLevel);
            row.Reset.Command.Execute(row.Reset.CommandParameter);
            Assert.Equal(PeripheralClaimLevel.Colors, second.PeripheralClaimLevel);
            Assert.Equal(PeripheralClaimLevel.HostMode, first.PeripheralClaimLevel);

            // Back to the first device, which no longer offers its saved
            // hold: taking the option out of the list changes neither the
            // saved hold nor what the picker shows.
            tab.DeviceConfig = first;
            Settle();
            Assert.Equal(2, row.Picker.SelectedIndex);
            PadPage.ShowLightingHold(row.Row, row.Picker, row.HostModeItem, 1);
            Settle();
            Assert.Equal(2, row.Picker.SelectedIndex);
            Assert.Same(row.HostModeItem, row.Picker.SelectedItem);
            Assert.Equal(PeripheralClaimLevel.HostMode, first.PeripheralClaimLevel);
            // A pick of an option still in the list is written as before.
            row.Picker.SelectedIndex = 1;
            Assert.Equal(PeripheralClaimLevel.Power, first.PeripheralClaimLevel);
        });

        /// <summary>A screen reader names every control of the row: the
        /// picker by the row's label, with the tooltip as its help text,
        /// and the reset by the label through its tooltip, which the
        /// button's style reads the name from. Each has an automation ID,
        /// the reset's in the form every per-setting reset on the page
        /// has.</summary>
        [Fact]
        public void EveryControlOfTheRow_HasAnAutomationIdAndAName() => RunSta(() =>
        {
            var s = Strings.Instance;
            var row = LoadRow();
            row.Root.DataContext = new Tab();
            Settle();

            Assert.Equal(s.Pad_Lighting_ClaimLevel, row.Label.Text);
            Assert.Equal("PeripheralClaimLevelCombo", AutomationProperties.GetAutomationId(row.Picker));
            Assert.Equal(s.Pad_Lighting_ClaimLevel, AutomationProperties.GetName(row.Picker));
            Assert.Equal(s.Pad_Lighting_ClaimLevel_Tooltip, row.Picker.ToolTip);

            // What a screen reader is handed for the picker. A control with
            // no help text of its own gives its tooltip (dotnet/wpf,
            // PresentationFramework, FrameworkElementAutomationPeer.cs:54-72),
            // so the picker without its last option reads the text that
            // stops before that option too.
            var peer = UIElementAutomationPeer.CreatePeerForElement(row.Picker);
            Assert.IsType<ComboBoxAutomationPeer>(peer);
            Assert.Equal("PeripheralClaimLevelCombo", peer.GetAutomationId());
            Assert.Equal(s.Pad_Lighting_ClaimLevel, peer.GetName());
            Assert.Equal(s.Pad_Lighting_ClaimLevel_Tooltip, peer.GetHelpText());
            PadPage.ShowLightingHold(row.Row, row.Picker, row.HostModeItem, 1);
            Settle();
            Assert.Equal(s.Pad_Lighting_ClaimLevel_TooltipNoHostMode, peer.GetHelpText());
            // A screen reader is told how many options the list holds. An
            // option that gives no number is counted among all the items,
            // the collapsed one included (dotnet/wpf, PresentationFramework,
            // ItemAutomationPeer.cs:297-351), so with the last option out
            // of the list each of the two left says two.
            int SetSize(ComboBoxItem option) => UIElementAutomationPeer.CreatePeerForElement(option).GetSizeOfSet();
            int unset = AutomationProperties.GetSizeOfSet(new ComboBoxItem());
            var listed = row.Picker.Items.Cast<ComboBoxItem>().Where(option => !ReferenceEquals(option, row.HostModeItem)).ToList();
            Assert.Equal(2, listed.Count);
            Assert.All(listed, option => Assert.Equal(2, SetSize(option)));
            // With it back none gives a number, and WPF counts the three.
            PadPage.ShowLightingHold(row.Row, row.Picker, row.HostModeItem, 2);
            Settle();
            Assert.All(listed, option => Assert.Equal(unset, SetSize(option)));
            Assert.Equal(unset, SetSize(row.HostModeItem));
            Assert.Equal(3, row.Picker.Items.Count);
            PadPage.ShowLightingHold(row.Row, row.Picker, row.HostModeItem, 1);
            Settle();
            Assert.All(listed, option => Assert.Equal(2, SetSize(option)));
            // Each option is named by its own text and has an ID of its own.
            Assert.Equal(new[] { s.Pad_Lighting_ClaimLevel_Colors, s.Pad_Lighting_ClaimLevel_Power, s.Pad_Lighting_ClaimLevel_HostMode },
                row.Picker.Items.Cast<ComboBoxItem>().Select(item => (string)item.Content));
            Assert.Equal(new[] { "PeripheralClaimLevelColorsItem", "PeripheralClaimLevelPowerItem", "PeripheralClaimLevelHostModeItem" },
                row.Picker.Items.Cast<ComboBoxItem>().Select(AutomationProperties.GetAutomationId));

            Assert.Equal("Reset_DeviceSlotConfig_PeripheralClaimLevel", AutomationProperties.GetAutomationId(row.Reset));
            Assert.Equal(s.Pad_Lighting_ClaimLevel, row.Reset.SettingLabel);
            Assert.Equal(SettingResetButton.FormatTooltip(s.Pad_ResetSection_Format, s.Pad_Lighting_ClaimLevel), row.Reset.ToolTip);
            Assert.Contains(s.Pad_Lighting_ClaimLevel, (string)row.Reset.ToolTip);

            // The style that turns the tooltip into the button's name.
            string app = RepoText("PadForge.App", "App.xaml");
            int style = app.IndexOf("<Style x:Key=\"SettingResetButton\"", StringComparison.Ordinal);
            Assert.True(style > 0);
            string body = app.Substring(style, app.IndexOf("</Style>", style, StringComparison.Ordinal) - style);
            Assert.Contains("<Setter Property=\"AutomationProperties.Name\" Value=\"{Binding ToolTip, RelativeSource={RelativeSource Self}", body);
        });

        /// <summary>The row sits in the peripheral lighting panel, under
        /// the switch it qualifies and above the route line, and no other
        /// control on the page carries its IDs. Its picker's width is the
        /// measured one (tools/combomeasure): the widest option needs 526.
        /// Its label takes its section's label column, which is wider than
        /// the widest label's 158.</summary>
        [Fact]
        public void TheRowSitsUnderTheSwitch_AndItsLabelTakesThePagesLabelColumn()
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var page = XDocument.Parse(RepoText("PadForge.App", "Views", "PadPage.xaml"));
            // Counted by hand: a failed Assert.Single would print the page.
            XElement One(Func<XElement, bool> match, string what)
            {
                var hits = page.Descendants().Where(match).ToList();
                Assert.True(hits.Count == 1, $"the page holds {hits.Count} of {what}");
                return hits[0];
            }
            XElement Named(string name) => One(n => (string)n.Attribute(x + "Name") == name, "an element named " + name);

            // The switch and the hold are the Lighting Control card's rows,
            // the hold last, and the route line stays with the tab's text
            // above the cards (#557: no setting sits outside a card).
            // The card folds to one line since #574, and its rows are the
            // fold card's one panel.
            var card = Named("PeripheralLightingCard");
            Assert.Equal("FoldCard", card.Name.LocalName);
            var rows = card.Elements().Single(e => e.Name.LocalName == "StackPanel")
                .Elements().Select(e => (string)e.Attribute(x + "Name")).ToList();
            int control = rows.IndexOf("PeripheralLightingControlRow");
            Assert.True(control >= 0);
            Assert.Equal("PeripheralClaimLevelRow", rows[control + 1]);
            Assert.Equal(control + 2, rows.Count);
            var panel = Named("PeripheralLightingPanel");
            Assert.Contains("PeripheralLightingRoute", panel.Elements().Select(e => (string)e.Attribute(x + "Name")));

            var row = Named("PeripheralClaimLevelRow");
            Assert.Equal("Collapsed", (string)row.Attribute("Visibility"));
            Assert.Equal(new[] { "TextBlock", "ComboBox", "SettingResetButton" }, row.Elements().Select(e => e.Name.LocalName));
            var label = row.Elements().ElementAt(0);
            var picker = row.Elements().ElementAt(1);
            var reset = row.Elements().ElementAt(2);
            Assert.Same(picker, Named("PeripheralClaimLevelPicker"));
            Assert.Same(picker.Elements().Last(), Named("PeripheralClaimLevelHostModeItem"));
            Assert.Same(picker, One(n => (string)n.Attribute("AutomationProperties.AutomationId") == "PeripheralClaimLevelCombo",
                "a control with the picker's automation ID"));
            Assert.Same(reset, One(n => (string)n.Attribute("AutomationProperties.AutomationId") == "Reset_DeviceSlotConfig_PeripheralClaimLevel",
                "a control with the reset's automation ID"));
            string[] options = { "PeripheralClaimLevelColorsItem", "PeripheralClaimLevelPowerItem", "PeripheralClaimLevelHostModeItem" };
            Assert.Equal(options, picker.Elements().Select(e => (string)e.Attribute("AutomationProperties.AutomationId")));
            foreach (string id in options)
                One(n => (string)n.Attribute("AutomationProperties.AutomationId") == id, "a control with the automation ID " + id);

            Assert.Equal(
                "{Binding DeviceConfig.PeripheralClaimLevel, Mode=TwoWay, Converter={StaticResource EnumIndexConverter}}",
                (string)picker.Attribute("SelectedIndex"));
            Assert.Equal("{Binding DeviceConfig.ResetPeripheralClaimLevelCommand}", (string)reset.Attribute("Command"));
            // A reset whose row can be hidden goes with its row.
            Assert.Null(reset.Attribute("Visibility"));

            // The row is one of the page's rows (#574): a SettingRow, which
            // puts a child docked right at the row's far right, so the
            // reset, which docks right through its style, sits there. Its
            // label takes the label column from the style and has no width
            // of its own, so the picker starts where every row's control
            // starts.
            Assert.Equal("SettingRow", row.Name.LocalName);
            Assert.Equal("44", (string)row.Attribute("MinHeight"));
            // The label style is the section's own, which shadows the
            // page's for everything in Lights & Sound: its board draws a
            // label column 170 wide, and the widest label needs 158.
            var rowLabelStyle = row.Ancestors().SelectMany(a => a.Elements())
                .Where(e => e.Name.LocalName == "ScrollViewer.Resources").SelectMany(r => r.Elements())
                .Single(n => (string)n.Attribute(x + "Key") == "DzLabel");
            int rowLabel = (int)rowLabelStyle.Elements().Single(e => (string)e.Attribute("Property") == "Width").Attribute("Value");
            Assert.Equal(170, rowLabel);
            Assert.Equal("{StaticResource DzLabel}", (string)label.Attribute("Style"));
            Assert.Null(label.Attribute("Width"));
            // The picker stops at the measured width and gives way in a
            // narrower column, where a width of its own ran it under the
            // reset.
            Assert.Null(picker.Attribute("Width"));
            int pickerWidth = (int)picker.Attribute("MaxWidth");
            Assert.True(pickerWidth >= 526 + 8, "the picker clips its widest option where it has the room");
            Assert.Contains(picker.Attributes(), a => a.Name.LocalName == "SettingRow.Fills" && a.Value == "True");
            Assert.True(rowLabel >= 158, "the label column clips its widest label");
        }

        /// <summary>Every setting of the Lighting and Force Feedback groups
        /// sits in a card, the way the rest of a device's groups are laid
        /// out. The Lighting Control switch and the Lighting Hold row once
        /// sat loose between the tab's text and its first card (owner,
        /// 2026-11-02). The group's text, its subtitle and route line, may
        /// sit outside. Each was a tab with a scroller of its own, and is a
        /// named group of a section since the page's six sections (#574).</summary>
        [Theory]
        [InlineData("TabLighting")]
        [InlineData("TabForceFeedback")]
        public void EverySettingOnTheOutputTabs_SitsInACard(string group)
        {
            var page = XDocument.Parse(RepoText("PadForge.App", "Views", "PadPage.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            // A group is a panel under its heading, or a group box with no
            // heading where its section's board draws none.
            var scroller = page.Descendants().Single(n => n.Name.LocalName is "StackPanel" or "GroupBox"
                && (string)n.Attribute(x + "Name") == group);
            string[] settings = { "CheckBox", "ComboBox", "Slider", "TextBox", "ToggleSwitch", "RadioButton", "SettingResetButton" };
            var loose = scroller.Descendants()
                .Where(n => settings.Contains(n.Name.LocalName))
                // A card is a border in the card style, or a card that
                // folds to one line (#574), which draws that border itself.
                .Where(n => !n.Ancestors().TakeWhile(a => a != scroller)
                    .Any(a => a.Name.LocalName == "FoldCard"
                        || (a.Name.LocalName == "Border" && ((string)a.Attribute("Style") ?? "").Contains("CardBorder"))))
                .Select(n => n.Name.LocalName + " " + ((string)n.Attribute("Content") ?? (string)n.Attribute("AutomationProperties.AutomationId") ?? ""))
                .ToList();
            Assert.True(loose.Count == 0, "outside a card: " + string.Join("; ", loose));
        }

        /// <summary>The tab asks what the selected device offers on every
        /// look at it, never for a vendor row or a unit row, and redraws
        /// when the hold changes. The route line takes the hold as its
        /// fourth argument, the number the dispatcher's claim carries: the
        /// device's own for a mouse or keyboard, and none for a row with no
        /// input of its own.</summary>
        [Fact]
        public void TheTabShowsTheRowAndNamesTheHold_ForTheSelectedDevice()
        {
            string page = RepoText("PadForge.App", "Views", "PadPage.xaml.cs");
            Assert.Contains("int offeredHold = hasPeripheralLighting && !vendorLightingRow ? OfferedLightingHold(lightingRow) : 0;", page);
            Assert.Contains("ShowLightingHold(PeripheralClaimLevelRow, PeripheralClaimLevelPicker, PeripheralClaimLevelHostModeItem, offeredHold);", page);

            Assert.Matches(new Regex(
                @"int claimLevel = vendorLightingRow \? 0\s*"
                + @": \(int\)\(routeVm\?\.DeviceConfig\?\.PeripheralClaimLevel \?\? ViewModels\.PeripheralClaimLevel\.Colors\);"), page);
            // The first three arguments as they were, then the hold.
            Assert.Matches(new Regex(
                @"PeripheralRouteText\.Lighting\(lightingRow, routeSlot,\s*"
                + @"slotLive && \(vendorLightingRow \|\| routeVm\?\.DeviceConfig\?\.PeripheralLightingEnabled == true\),\s*"
                + @"claimLevel\)"), page);

            Assert.Matches(new Regex(
                @"case nameof\(ViewModels\.DeviceSlotConfig\.PeripheralLightingEnabled\):\s*"
                + @"case nameof\(ViewModels\.DeviceSlotConfig\.PeripheralClaimLevel\):\s*"
                + @"SyncTabVisibility\(\);"), page);
        }

        /// <summary>The dispatcher takes the switch and the hold of a
        /// device from one read of the pair, and reads neither on its own
        /// while it lights the device.</summary>
        [Fact]
        public void TheDispatcherReadsTheSwitchAndTheHold_InOneRead()
        {
            string source = RepoText("PadForge.App", "Common", "Input", "UserEffectsDispatcher.cs");
            int from = source.IndexOf("private bool LightPeripheral(", StringComparison.Ordinal);
            int to = source.IndexOf("private void DispatchSnapshot(", StringComparison.Ordinal);
            Assert.True(from >= 0 && to > from, "the method that lights one device");
            string body = source.Substring(from, to - from);
            Assert.Contains("devCfg.PeripheralLightingClaim(out hold)", body);
            Assert.Contains("int level = outputOnly ? 0 : (int)hold;", body);
            Assert.DoesNotContain("devCfg.PeripheralLightingEnabled", body);
            Assert.DoesNotContain("devCfg.PeripheralClaimLevel", body);
        }

        // ─────────────────────────────────────────────
        //  The tooltip of a picker without its last option
        // ─────────────────────────────────────────────

        /// <summary>The full tooltip ends by saying when to choose the last
        /// option and what that switches off. A device that offers only the
        /// first hold has a picker of two options, where "the last option"
        /// would name the wrong one, so that picker shows the same text up
        /// to those sentences: it names the two options the picker has, in
        /// each locale's own words, and says nothing of the on-board memory
        /// mode that neither of them touches.</summary>
        [Theory]
        [InlineData("", "on-board memory mode")]
        [InlineData("de", "Onboard-Modus")]
        [InlineData("es", "modo de memoria integrada")]
        [InlineData("fr", "mode mémoire intégrée")]
        [InlineData("it", "modalità memoria integrata")]
        [InlineData("ja", "オンボードモード")]
        [InlineData("ko", "온보드 모드")]
        [InlineData("nl", "onboardmodus")]
        [InlineData("pt-BR", "modo de memória integrada")]
        [InlineData("zh-Hans", "板载模式")]
        public void TheTooltipWithoutTheLastOption_IsTheFullOneUpToThatOption_InEveryLocale(string locale, string mode)
        {
            var set = Strings.ResourceManager.GetResourceSet(new CultureInfo(locale), true, false);
            string full = set.GetString("Pad_Lighting_ClaimLevel_Tooltip");
            string without = set.GetString("Pad_Lighting_ClaimLevel_TooltipNoHostMode");
            Assert.False(string.IsNullOrWhiteSpace(without), locale);
            Assert.StartsWith(without, full, StringComparison.Ordinal);
            Assert.Equal(without, without.Trim());
            // It ends on a whole sentence, and the full text goes on.
            Assert.Contains(without[^1], ".。");
            string rest = full.Substring(without.Length);
            Assert.True(rest.Trim().Length > 40, locale);

            Assert.Contains(set.GetString("Pad_Lighting_ClaimLevel_Colors"), without);
            Assert.Contains(set.GetString("Pad_Lighting_ClaimLevel_Power"), without);
            Assert.DoesNotContain(mode, without, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(mode, rest, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheTooltipWithoutTheLastOption_ReadsAsWritten()
        {
            const string written = "Colors Only leaves the device\u2019s power saving and its saved profile alone. Choose Colors and Lighting Power when the device\u2019s own idle effect takes the color away. The device then stops dimming its lights by itself.";
            Assert.Equal(written,
                Strings.ResourceManager.GetString("Pad_Lighting_ClaimLevel_TooltipNoHostMode", CultureInfo.InvariantCulture));
            // The designer names it, and the page's code reads it by that name.
            var s = Strings.Instance;
            Assert.Equal(written,
                Strings.ResourceManager.GetString(nameof(s.Pad_Lighting_ClaimLevel_TooltipNoHostMode), CultureInfo.InvariantCulture));
            Assert.False(string.IsNullOrWhiteSpace(s.Pad_Lighting_ClaimLevel_TooltipNoHostMode));
        }
    }
}
