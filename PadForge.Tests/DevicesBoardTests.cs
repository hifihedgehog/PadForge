using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using System.Xml.Linq;
using PadForge.Common;
using PadForge.Controls;
using PadForge.Engine;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Devices page as the world-class pass draws it (#574): the
    /// connected list in three parts under a head that names its columns,
    /// a row's link, battery and input light, the table's narrow form, the
    /// panel's picture and tiles, Test Inputs before Settings, and the
    /// chips of the buttons held. It reads the language's own words, so it
    /// runs with the tests that do.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class DevicesBoardTests
    {
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        private const string DualSenseUsb = @"\\?\HID#VID_054C&PID_0CE6&MI_03#8&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private const string DualSenseBluetooth = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0ce6#9&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private const string MousePath = @"\\?\HID#VID_046D&PID_C08B&MI_00#8&4&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";
        private const string KeyboardPath = @"\\?\HID#VID_046D&PID_C33F&MI_00#8&5&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}";

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        private static XDocument Page() => XDocument.Parse(RepoText("PadForge.App", "Views", "DevicesPage.xaml"));

        private static XElement Named(XDocument page, string name)
            => page.Descendants().Single(e => (string)e.Attribute(Xaml + "Name") == name);

        private static XElement Keyed(XDocument page, string key)
            => page.Descendants().Single(e => (string)e.Attribute(Xaml + "Key") == key);

        private static string Attr(XElement e, string name)
            => e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

        private static string S(string key) => "{Binding " + key + ", Source={x:Static strings:Strings.Instance}}";

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
            Assert.True(thread.Join(30000), "the Devices board test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static DeviceRowViewModel Row(string name, string typeKey, string path, bool online, ushort vid = 0, ushort pid = 0)
            => new DeviceRowViewModel
            {
                InstanceGuid = Guid.NewGuid(), DeviceName = name, ProductName = name,
                DeviceTypeKey = typeKey, DevicePath = path, VendorId = vid, ProductId = pid, IsOnline = online,
            };

        // ── A row's part, link and battery ──

        [Fact]
        public void ARowKnowsItsPartItsLinkAndWhetherItsBatteryIsLow()
        {
            var s = Strings.Instance;
            var pad = Row("DualSense Wireless Controller", "Gamepad", DualSenseBluetooth, true, 0x054C, 0x0CE6);
            Assert.Equal(DeviceFilter.Controllers, pad.Section);
            Assert.True(pad.IsControllerRow);
            Assert.True(pad.IsBluetoothLink, "control: the Bluetooth path is a Bluetooth link");
            Assert.True(pad.HasLink);
            Assert.Equal(s.Pad_Transport_Bluetooth, pad.LinkText);
            Assert.True(pad.HasLinkOrBattery);

            // A path that is not Bluetooth claims no link at all.
            var usb = Row("DualSense Wireless Controller", "Gamepad", DualSenseUsb, true, 0x054C, 0x0CE6);
            Assert.False(usb.HasLink);
            Assert.Equal(string.Empty, usb.LinkText);
            Assert.False(usb.HasLinkOrBattery);
            usb.BatteryPercent = 50;
            Assert.True(usb.HasLinkOrBattery);

            var mouse = Row("Mouse", "Mouse", MousePath, true, 0x046D, 0xC08B);
            Assert.Equal(DeviceFilter.KeyboardsMice, mouse.Section);
            Assert.False(mouse.IsControllerRow);
            Assert.Equal(DeviceFilter.Other, Row("Microphone", "Microphone", "mic://capture/0", true).Section);

            // Low is at or under the level, and never while charging.
            Assert.False(pad.BatteryLow);
            Assert.Equal(string.Empty, pad.BatterySpoken);
            pad.BatteryPercent = 16;
            Assert.False(pad.BatteryLow);
            Assert.Equal(string.Format(s.Home_FirstRun_Battery_Format, "16%"), pad.BatterySpoken);
            pad.BatteryPercent = 15;
            Assert.True(pad.BatteryLow);
            Assert.Equal(string.Format(s.Home_Battery_Low_Format, "15%"), pad.BatterySpoken);
            pad.BatteryCharging = true;
            Assert.False(pad.BatteryLow);
            Assert.Equal(string.Format(s.Home_Battery_Charging_Format, "15%"), pad.BatterySpoken);
            pad.BatteryCharging = false;
            pad.BatteryLowAt = 10;
            Assert.False(pad.BatteryLow);
            pad.BatteryLowAt = 20;
            Assert.True(pad.BatteryLow);
            pad.BatteryPercent = 0;
            Assert.True(pad.BatteryLow, "an empty battery is low");
        }

        [Fact]
        public void ARowsWordsAreRaisedWithWhatTheyAreMadeOf()
        {
            var row = new DeviceRowViewModel();
            var raised = new List<string>();
            row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            row.DeviceTypeKey = "Gamepad";
            Assert.Contains(nameof(DeviceRowViewModel.Section), raised);
            Assert.Contains(nameof(DeviceRowViewModel.IsControllerRow), raised);

            raised.Clear();
            row.DevicePath = DualSenseBluetooth;
            Assert.Contains(nameof(DeviceRowViewModel.HasLink), raised);
            Assert.Contains(nameof(DeviceRowViewModel.LinkText), raised);
            Assert.Contains(nameof(DeviceRowViewModel.HasLinkOrBattery), raised);

            raised.Clear();
            row.BatteryPercent = 40;
            Assert.Contains(nameof(DeviceRowViewModel.BatteryLow), raised);
            Assert.Contains(nameof(DeviceRowViewModel.BatterySpoken), raised);
            Assert.Contains(nameof(DeviceRowViewModel.HasLinkOrBattery), raised);

            // The level: raised when it moves the battery across it, and
            // not when it does not.
            raised.Clear();
            row.BatteryLowAt = 30;
            Assert.Empty(raised);
            row.BatteryLowAt = 40;
            Assert.Contains(nameof(DeviceRowViewModel.BatteryLow), raised);
            Assert.Contains(nameof(DeviceRowViewModel.BatterySpoken), raised);

            raised.Clear();
            row.BatteryCharging = true;
            Assert.Contains(nameof(DeviceRowViewModel.BatteryLow), raised);

            raised.Clear();
            row.IsSending = true;
            row.IsSending = true;
            Assert.Equal(new[] { nameof(DeviceRowViewModel.IsSending) }, raised);
        }

        // ── The connected list's parts ──

        /// <summary>A list's parts as they stand: each part's name, then its rows' names.</summary>
        private static string[] Parts(System.ComponentModel.ICollectionView view)
            => view.Groups.Cast<CollectionViewGroup>()
                .Select(g => g.Name + ": " + string.Join(", ", g.Items.Cast<DeviceRowViewModel>().Select(d => d.DeviceName))).ToArray();

        /// <summary>The Connected list stands in the Show segments' parts and
        /// order, whatever order the rows have in the collection, which is
        /// by name. Within a part the rows keep the collection's order, and
        /// a part with no row is not there.</summary>
        [Fact]
        public void TheConnectedListStandsInItsPartsInOrder() => RunSta(() =>
        {
            var vm = new DevicesViewModel();
            var keyboard = Row("A Keyboard", "Keyboard", KeyboardPath, true, 0x046D, 0xC33F);
            var padB = Row("B Pad", "Gamepad", DualSenseUsb, true, 0x054C, 0x0CE6);
            var midi = Row("C Midi", "Midi", "midi://in/0", true);
            var mouse = Row("D Mouse", "Mouse", MousePath, true, 0x046D, 0xC08B);
            var padE = Row("E Pad", "Gamepad", DualSenseBluetooth, true, 0x054C, 0x0CE6);
            foreach (var row in new[] { keyboard, padB, midi, mouse, padE }) vm.Devices.Add(row);
            vm.RefreshCounts();
            vm.EnsureListViews();
            Assert.Equal(DeviceListGroup.Connected, midi.ListGroup);

            Assert.Equal(new[] { "Controllers: B Pad, E Pad", "KeyboardsMice: A Keyboard, D Mouse", "Other: C Midi" }, Parts(vm.ConnectedView));
            Assert.Equal(new[] { padB, padE, keyboard, mouse, midi }, vm.ConnectedView.Cast<DeviceRowViewModel>().ToArray());
            Assert.Equal(vm.RowsOf(DeviceListGroup.Connected, inParts: true), vm.ConnectedView.Cast<DeviceRowViewModel>());
            // The two folded lists have no parts.
            Assert.Null(vm.NotConnectedView.Groups);
            Assert.Null(vm.OtherInputsView.Groups);

            // A part's rows all go: the part goes with them.
            padB.IsOnline = false;
            padE.IsOnline = false;
            vm.RefreshCounts();
            Assert.Equal(new[] { "KeyboardsMice: A Keyboard, D Mouse", "Other: C Midi" }, Parts(vm.ConnectedView));
            // And comes back first, though it is the last to arrive.
            padE.IsOnline = true;
            vm.RefreshCounts();
            Assert.Equal(new[] { "Controllers: E Pad", "KeyboardsMice: A Keyboard, D Mouse", "Other: C Midi" }, Parts(vm.ConnectedView));

            // The segment leaves one part.
            vm.Filter = DeviceFilter.KeyboardsMice;
            Assert.Equal(new[] { "KeyboardsMice: A Keyboard, D Mouse" }, Parts(vm.ConnectedView));
            vm.Filter = DeviceFilter.All;

            // A row whose type changes moves to its new part.
            mouse.DeviceTypeKey = "Midi";
            vm.RefreshCounts();
            Assert.Equal(new[] { "Controllers: E Pad", "KeyboardsMice: A Keyboard", "Other: C Midi, D Mouse" }, Parts(vm.ConnectedView));

            // It moves also where the rows' order from first to last stays
            // what it was: the keyboard is the one row of its part, right
            // after the pad, and as a pad it is the second of the pad's.
            var two = new DevicesViewModel();
            var first = Row("X Pad", "Gamepad", DualSenseUsb, true, 0x054C, 0x0CE6);
            var second = Row("Y Keyboard", "Keyboard", KeyboardPath, true, 0x046D, 0xC33F);
            two.Devices.Add(first);
            two.Devices.Add(second);
            two.RefreshCounts();
            two.EnsureListViews();
            Assert.Equal(new[] { "Controllers: X Pad", "KeyboardsMice: Y Keyboard" }, Parts(two.ConnectedView));
            second.DeviceTypeKey = "Gamepad";
            Assert.Equal(new[] { first, second }, two.ConnectedView.Cast<DeviceRowViewModel>().ToArray());
            Assert.Equal(new[] { first, second }, two.RowsOf(DeviceListGroup.Connected, inParts: true).ToArray());
            two.RefreshCounts();
            Assert.Equal(new[] { "Controllers: X Pad, Y Keyboard" }, Parts(two.ConnectedView));
        });

        [Fact]
        public void ThePartsAreOrderedByTheShowSegments()
        {
            var order = DevicesViewModel.SectionOrder.Instance;
            Assert.Equal(new[] { DeviceFilter.All, DeviceFilter.Controllers, DeviceFilter.KeyboardsMice, DeviceFilter.Other },
                Enum.GetValues<DeviceFilter>());
            string view = RepoText("PadForge.App", "ViewModels", "DevicesViewModel.List.cs");
            Assert.Contains("view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DeviceRowViewModel.Section))", view);
            Assert.Contains("CustomSort = SectionOrder.Instance,", view);
            // What is no part of the list sorts last and never throws.
            Assert.Equal(0, order.Compare("x", null));
        }

        /// <summary>The table's head names Link and Battery while a row the
        /// lists show has one, so a desk of wired pads and keyboards has no
        /// empty column.</summary>
        [Fact]
        public void TheTableNamesAColumnWhileARowHasItsFact()
        {
            var vm = new DevicesViewModel();
            var usb = Row("Wired Pad", "Gamepad", DualSenseUsb, true, 0x054C, 0x0CE6);
            vm.Devices.Add(usb);
            vm.Devices.Add(Row("Keyboard", "Keyboard", KeyboardPath, true, 0x046D, 0xC33F));
            vm.RefreshCounts();
            Assert.False(vm.ShowLinkColumn);
            Assert.False(vm.ShowBatteryColumn);

            var wireless = Row("Wireless Pad", "Gamepad", DualSenseBluetooth, true, 0x054C, 0x0CE6);
            vm.Devices.Add(wireless);
            vm.RefreshCounts();
            Assert.True(vm.ShowLinkColumn);
            Assert.False(vm.ShowBatteryColumn);

            // The battery walk changes no count, so it reads the columns itself.
            usb.BatteryPercent = 70;
            Assert.False(vm.ShowBatteryColumn);
            vm.RefreshColumns();
            Assert.True(vm.ShowBatteryColumn);

            // A segment that hides the rows hides their columns.
            vm.Filter = DeviceFilter.KeyboardsMice;
            Assert.False(vm.ShowLinkColumn);
            Assert.False(vm.ShowBatteryColumn);
            vm.Filter = DeviceFilter.All;
            Assert.True(vm.ShowLinkColumn && vm.ShowBatteryColumn);

            string service = RepoText("PadForge.App", "Services", "InputService.cs");
            int walk = service.IndexOf("private void UpdateBatteryIndicators()", StringComparison.Ordinal);
            Assert.True(walk > 0);
            string body = service.Substring(walk, service.IndexOf("private readonly Dictionary<Guid, (int LastPct, bool Notified)>", walk, StringComparison.Ordinal) - walk);
            Assert.Contains("row.BatteryLowAt = lowAt;", body);
            Assert.Contains("_mainVm.Devices.RefreshColumns();", body);
        }

        // ── The panel's picture ──

        /// <summary>A device's picture is the controller it is: the art of
        /// the first preset that has the device's own vendor and product
        /// ids and art of its own. A device no preset names has none, and
        /// its panel shows its kind.</summary>
        [Fact]
        public void ADevicesPictureIsTheControllerItIs()
        {
            Assert.Equal(("DualSense", "DualSense"), StagePictures.ForDevice(0x054C, 0x0CE6));
            Assert.Equal(("XBOX360", "XBOX360"), StagePictures.ForDevice(0x045E, 0x028E));
            Assert.Equal(((string)null, (string)null), StagePictures.ForDevice(0, 0));
            Assert.Equal(((string)null, (string)null), StagePictures.ForDevice(0x046D, 0xC08B));

            // Every preset with art of its own answers for its own ids.
            int own = 0;
            foreach (var preset in PadForge.Common.Input.HMaestroProfileCatalog.AllProfiles)
            {
                PadForge.Common.Input.HMaestroProfileCatalog.ResolveAssetFolders(preset.Id, VirtualControllerType.Xbox, out bool dedicated);
                if (!dedicated) continue;
                own++;
                Assert.NotNull(StagePictures.ForDevice(preset.VendorId, preset.ProductId).Folder);
            }
            Assert.True(own >= 10, own + " presets have art of their own");

            Assert.StartsWith("2DModels/DualSense/", StagePictures.FirstPath("DualSense"));
            Assert.Equal(PadForge.Models2D.DualShock3Layout.BasePath, StagePictures.FirstPath("DS3"));
        }

        [Fact]
        public void ThePanelsPictureIsAskedForOnceForADeviceAndAView()
        {
            var vm = new DevicesViewModel();
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            Guid device = Guid.NewGuid();
            Assert.False(vm.PictureIsFor(device, 0x054C, 0x0CE6, true));
            Assert.True(vm.ShowSelectedKind);
            Assert.False(vm.HasSelectedPicture);

            // A still on its way: nothing shows for the moment, not the kind.
            vm.SetPicture(device, 0x054C, 0x0CE6, true, null, null, coming: true);
            Assert.True(vm.PictureIsFor(device, 0x054C, 0x0CE6, true));
            Assert.False(vm.ShowSelectedKind);
            Assert.False(vm.HasSelectedPicture);
            Assert.Contains(nameof(DevicesViewModel.ShowSelectedKind), raised);

            // The still is ready: the page asks again and gets it.
            vm.ForgetPicture();
            Assert.False(vm.PictureIsFor(device, 0x054C, 0x0CE6, true));
            var color = new System.Windows.Media.Imaging.WriteableBitmap(2, 2, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null);
            var gray = new System.Windows.Media.Imaging.WriteableBitmap(2, 2, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null);
            raised.Clear();
            vm.SetPicture(device, 0x054C, 0x0CE6, true, color, gray);
            Assert.Same(color, vm.SelectedPicture);
            Assert.Same(gray, vm.SelectedPictureGray);
            Assert.True(vm.HasSelectedPicture);
            Assert.False(vm.ShowSelectedKind);
            Assert.Contains(nameof(DevicesViewModel.SelectedPicture), raised);
            Assert.Contains(nameof(DevicesViewModel.SelectedPictureGray), raised);
            Assert.Contains(nameof(DevicesViewModel.HasSelectedPicture), raised);

            // The same picture set again raises nothing.
            raised.Clear();
            vm.SetPicture(device, 0x054C, 0x0CE6, true, color, gray);
            Assert.Empty(raised);

            // Another device, other ids or the other view is another picture.
            Assert.False(vm.PictureIsFor(Guid.NewGuid(), 0x054C, 0x0CE6, true));
            Assert.False(vm.PictureIsFor(device, 0x054C, 0x0DF2, true));
            Assert.False(vm.PictureIsFor(device, 0x054C, 0x0CE6, false));

            // A device with no picture shows its kind.
            vm.SetPicture(device, 0x046D, 0xC08B, true, null, null);
            Assert.True(vm.ShowSelectedKind);
        }

        /// <summary>The service asks for the picture wherever it can change:
        /// on the page's lane, when a device is picked (the lane rests while
        /// the engine is stopped), when the page is shown and when a still
        /// has been made. It lights the rows on the same lane, and a stopped
        /// engine leaves none lit.</summary>
        [Fact]
        public void TheServiceLightsTheRowsAndAsksForThePicture()
        {
            string service = RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("""
                UpdateDevicesRawState();
                UpdateDeviceRowLights();
                UpdateSelectedDevicePicture();
""".ReplaceLineEndings(), service.ReplaceLineEndings());
            Assert.Contains("if (value) UpdateSelectedDevicePicture();", service);
            int picked = service.IndexOf("private void OnDevicesVmPropertyChanged(", StringComparison.Ordinal);
            Assert.True(picked > 0);
            string onPick = service.Substring(picked, 900);
            Assert.True(onPick.IndexOf("UpdateSelectedDevicePicture();", StringComparison.Ordinal)
                < onPick.IndexOf("if (_inputManager != null && _inputManager.IsRunning)", StringComparison.Ordinal),
                "the picture is asked for before the engine's state ends the handler");
            int made = service.IndexOf("private void StillMade()", StringComparison.Ordinal);
            string onMade = service.Substring(made, 700);
            Assert.Contains("_mainVm.Devices.ForgetPicture();", onMade);
            Assert.Contains("UpdateSelectedDevicePicture();", onMade);

            Assert.Contains("private readonly InputActivity _rowActivity = new();", service);
            Assert.Contains("_rowActivity.Forget(row.InstanceGuid);", service);
            Assert.Contains("&& _rowActivity.Look(row.InstanceGuid, ud.InputState, ud.CapType == InputDeviceType.Gamepad, nowMs);", service);
            Assert.Contains("""
                    row.IsOnline = false;
                    row.IsSending = false;
""".ReplaceLineEndings(), service.ReplaceLineEndings());
        }

        // ── The compact test ──

        [Fact]
        public void TheGamepadTestListsTheHeldButtons()
        {
            var vm = new DevicesViewModel();
            vm.RebuildRawStateCollections(new[] { 0, 1, 2, 3, 4, 5 }, Enumerable.Range(0, 15).ToArray(), 1, gamepadLayout: true);
            Assert.Empty(vm.TestPressed);
            Assert.True(vm.NothingPressed);

            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            var state = new CustomInputState
            {
                Axis = new int[CustomInputState.MaxAxis],
                Buttons = new bool[CustomInputState.MaxButtons],
            };
            state.Buttons[0] = true;
            state.Buttons[4] = true;
            vm.UpdateGamepadTest(state);
            Assert.Equal(new[]
            {
                PadForge.Common.MappingDisplayResolver.LocalizeObjectName(GamepadObjectNames.Button(0)),
                PadForge.Common.MappingDisplayResolver.LocalizeObjectName(GamepadObjectNames.Button(4)),
            }, vm.TestPressed);
            Assert.False(vm.NothingPressed);
            Assert.Equal(string.Join(", ", vm.TestPressed), vm.TestPressedNames);
            Assert.Contains(nameof(DevicesViewModel.TestPressed), raised);
            Assert.Contains(nameof(DevicesViewModel.NothingPressed), raised);

            // The same buttons held: the chips are left alone.
            raised.Clear();
            vm.UpdateGamepadTest(state);
            Assert.DoesNotContain(nameof(DevicesViewModel.TestPressed), raised);

            state.Buttons[0] = state.Buttons[4] = false;
            vm.UpdateGamepadTest(state);
            Assert.Empty(vm.TestPressed);
            Assert.True(vm.NothingPressed);

            state.Buttons[1] = true;
            vm.UpdateGamepadTest(state);
            Assert.Single(vm.TestPressed);
            vm.ClearRawState();
            vm.RebuildRawStateCollections(new[] { 0, 1, 2, 3, 4, 5 }, Enumerable.Range(0, 15).ToArray(), 1, gamepadLayout: true);
            Assert.Empty(vm.TestPressed);
        }

        // ── The table's form ──

        [Fact]
        public void TheListShowsLinkAndBatteryAsColumnsFrom660() => RunSta(() =>
        {
            Assert.False(DevicesPage.IsWide(659.5));
            Assert.True(DevicesPage.IsWide(660));
            // The room the columns and the row's insets take, and what is
            // then left a name at the least width.
            const double insets = 18 + 16, tile = 40, gap = 14, link = 104, battery = 108, usedBy = 104;
            Assert.Equal(DevicesPage.TableMin - 214, insets + tile + gap + (gap + link) + (gap + battery) + (gap + usedBy));

            // Every row and the head read it from the list card.
            var metadata = (FrameworkPropertyMetadata)DevicesPage.IsWideTableProperty.GetMetadata(typeof(FrameworkElement));
            Assert.True(metadata.Inherits);
            Assert.True((bool)metadata.DefaultValue);
            var card = new System.Windows.Controls.Border();
            var row = new System.Windows.Controls.Border();
            card.Child = row;
            Assert.True(DevicesPage.GetIsWideTable(row));
            DevicesPage.SetIsWideTable(card, false);
            Assert.False(DevicesPage.GetIsWideTable(row));
        });

        // ── The page's parts ──

        [Fact]
        public void TheListIsATableInOneScope()
        {
            var page = Page();
            var card = Named(page, "DeviceListCard");
            Assert.Equal("ListCard_SizeChanged", Attr(card, "SizeChanged"));
            var stack = card.Elements().Single();
            Assert.Equal("True", Attr(stack, "Grid.IsSharedSizeScope"));

            // The head's columns are the rows' own.
            string[] Columns(XElement grid) => grid.Elements().Single(e => e.Name.LocalName == "Grid.ColumnDefinitions").Elements()
                .Select(c => Attr(c, "Width") + "/" + Attr(c, "SharedSizeGroup")).ToArray();
            var head = Named(page, "ListHead");
            var template = Keyed(page, "DeviceRowTemplate");
            var row = template.Elements().Single(e => e.Name.LocalName == "Border");
            var rowGrid = row.Elements().Single(e => e.Name.LocalName == "Grid");
            Assert.Same(rowGrid, Named(page, "Row"));
            // The Border is known to the accessibility allowlist by its
            // drag handlers, which a name of its own would hide.
            Assert.Null(row.Attribute(Xaml + "Name"));
            Assert.Equal(new[] { "40/", "*/", "Auto/DeviceLink", "Auto/DeviceBattery", "Auto/DeviceUsedBy" }, Columns(head));
            Assert.Equal(Columns(head), Columns(rowGrid));
            Assert.Equal("18,12,16,10", Attr(head, "Margin"));
            Assert.Equal("18,0,16,0", Attr(row, "Padding"));

            var labels = head.Elements().Where(e => e.Name.LocalName == "TextBlock").ToList();
            Assert.Equal(new[] { S("Devices_Group_Connected"), S("Devices_Detail_Link"), S("Devices_Detail_Battery"), S("Devices_UsedBy") },
                labels.Select(l => Attr(l, "Text")).ToArray());
            Assert.Equal(new[] { "1", "2", "3", "4" }, labels.Select(l => Attr(l, "Grid.Column")).ToArray());
            // Link and Battery are named while a row has the fact and the
            // list is wide.
            foreach (var (label, flag) in new[] { (labels[1], "ShowLinkColumn"), (labels[2], "ShowBatteryColumn") })
            {
                var conditions = label.Descendants().Where(e => e.Name.LocalName == "Condition").Select(c => Attr(c, "Binding")).ToArray();
                Assert.Equal(new[] { "{Binding " + flag + "}", "{Binding (local:DevicesPage.IsWideTable), RelativeSource={RelativeSource Self}}" }, conditions);
                Assert.Contains(label.Descendants(), e => e.Name.LocalName == "Setter" && Attr(e, "Property") == "Visibility" && Attr(e, "Value") == "Collapsed");
            }
            // A cell is as wide as its column's name at the least.
            Assert.Equal(new[] { "104", "108", "104" }, labels.Skip(1).Select(l => Attr(l, "MinWidth")).ToArray());
            Assert.Equal("104", Attr(Named(page, "LinkCell"), "MinWidth"));
            Assert.Equal("108", Attr(Named(page, "BatteryCell"), "MinWidth"));

            // The list card's parts in order: the head, the connected
            // devices in their parts, then the two folds.
            var all = page.Descendants().ToList();
            var order = new[] { "ListHead", "NoMatchLine", "ConnectedList", "OtherInputsFold", "NotConnectedFold" }
                .Select(n => all.IndexOf(Named(page, n))).ToList();
            Assert.Equal(order.OrderBy(i => i), order);
            var groupStyle = Named(page, "ConnectedList").Descendants().Single(e => e.Name.LocalName == "GroupStyle");
            Assert.Equal("{StaticResource DeviceGroup}", Attr(groupStyle, "ContainerStyle"));
            Assert.DoesNotContain(Named(page, "NotConnectedList").Descendants(), e => e.Name.LocalName == "GroupStyle");

            // A fold's chevron stands under the rows' tiles and its title
            // under their names.
            foreach (string fold in new[] { "OtherInputsFold", "NotConnectedFold" })
            {
                var part = Named(page, fold);
                Assert.Equal("33,0,16,0", Attr(part, "Padding"));
                Assert.Equal("0,0,29,0", Attr(part, "ChevronMargin"));
                Assert.Equal("52", Attr(part, "HeaderMinHeight"));
                Assert.Null(Attr(part, "Summary"));
            }
            const double chevron = 10;
            Assert.Equal(18 + 40 / 2.0, 33 + chevron / 2);
            Assert.Equal(18 + 40 + 14.0, 33 + chevron + 29);

            string text = RepoText("PadForge.App", "Views", "DevicesPage.xaml");
            Assert.DoesNotContain("ConnectedShown", text);
            Assert.DoesNotContain("{Binding Devices_NotInUse", text);
        }

        [Fact]
        public void ARowIsTallerForAControllerAndLitWhileItSends()
        {
            var page = Page();
            var template = Keyed(page, "DeviceRowTemplate");
            Assert.Equal("47", Attr(Named(page, "Row"), "MinHeight"));
            var triggers = template.Elements().Single(e => e.Name.LocalName == "DataTemplate.Triggers").Elements().ToList();
            Dictionary<string, string> Setters(XElement trigger) => trigger.Elements()
                .ToDictionary(s => Attr(s, "TargetName") + "." + Attr(s, "Property"), s => Attr(s, "Value"));

            var controller = Setters(triggers.Single(t => Attr(t, "Binding") == "{Binding IsControllerRow}"));
            Assert.Equal("63", controller["Row.MinHeight"]);
            Assert.Equal("40", controller["Tile.Width"]);
            Assert.Equal("40", controller["Tile.Height"]);
            Assert.Equal("Collapsed", controller["OneLine.Visibility"]);
            Assert.Equal("Visible", controller["TwoLines.Visibility"]);

            // The tile's light is the device's input, and ember is for that
            // alone: the picked row's glyph is the text's own color.
            var halo = Named(page, "Tile").Parent;
            Assert.Equal("Halo", halo.Name.LocalName);
            Assert.Equal("{Binding IsSending}", Attr(halo, "IsLit"));
            Assert.Equal("17.5", Attr(halo, "Radius"));
            Assert.Equal("0.2", Attr(halo, "Strength"));
            var sending = Setters(triggers.Single(t => Attr(t, "Binding") == "{Binding IsSending}"));
            Assert.Equal("{DynamicResource EmberRingBrush}", sending["Tile.BorderBrush"]);
            Assert.Equal("{DynamicResource EmberHotBrush}", sending["Glyph.Foreground"]);
            var picked = Setters(triggers.Single(t => (Attr(t, "Binding") ?? string.Empty).StartsWith("{Binding IsSelected,", StringComparison.Ordinal)));
            Assert.Equal("{DynamicResource TextFillColorPrimaryBrush}", picked["Glyph.Foreground"]);

            // A name runs on into the columns its row has nothing in.
            var noLink = Setters(triggers.Single(t => Attr(t, "Binding") == "{Binding HasLink}"));
            Assert.Equal("2", noLink["OneLine.Grid.ColumnSpan"]);
            Assert.Equal("2", noLink["TwoLines.Grid.ColumnSpan"]);
            var neither = Setters(triggers.Single(t => Attr(t, "Binding") == "{Binding HasLinkOrBattery}"));
            Assert.Equal("3", neither["OneLine.Grid.ColumnSpan"]);
            Assert.Equal("3", neither["TwoLines.Grid.ColumnSpan"]);
            Assert.True(triggers.FindIndex(t => Attr(t, "Binding") == "{Binding HasLink}")
                < triggers.FindIndex(t => Attr(t, "Binding") == "{Binding HasLinkOrBattery}"), "the wider span is set last");

            // A narrow list: the two facts after the type, and no column.
            var narrow = triggers.Single(t => t.Name.LocalName == "Trigger");
            Assert.Equal("Row", Attr(narrow, "SourceName"));
            Assert.Equal("local:DevicesPage.IsWideTable", Attr(narrow, "Property"));
            Assert.Equal("False", Attr(narrow, "Value"));
            Assert.Equal(new Dictionary<string, string>
            {
                ["LinkCell.Visibility"] = "Collapsed", ["BatteryCell.Visibility"] = "Collapsed",
                ["OneLineFacts.Visibility"] = "Visible", ["TwoLinesFacts.Visibility"] = "Visible",
            }, Setters(narrow));
            foreach (string facts in new[] { "OneLineFacts", "TwoLinesFacts" })
            {
                var panel = Named(page, facts);
                Assert.Equal("Collapsed", Attr(panel, "Visibility"));
                Assert.Equal(new[] { "{StaticResource DeviceLinkFact}", "{StaticResource DeviceBatteryFact}" },
                    panel.Elements().Select(e => Attr(e, "ContentTemplate")).ToArray());
            }
            Assert.Equal("{StaticResource DeviceLinkFact}", Attr(Named(page, "LinkCell"), "ContentTemplate"));
            Assert.Equal("{StaticResource DeviceBatteryFact}", Attr(Named(page, "BatteryCell"), "ContentTemplate"));

            // The picked row: an ember bar at its start and a wash.
            var container = Keyed(page, "DeviceRowContainer");
            var bar = container.Descendants().Single(e => (string)e.Attribute(Xaml + "Name") == "Bar");
            Assert.Equal("3", Attr(bar, "Width"));
            Assert.Equal("{DynamicResource EmberBrush}", Attr(bar, "Background"));
            var wash = container.Descendants().Single(e => (string)e.Attribute(Xaml + "Name") == "Wash");
            Assert.Equal(new[] { "#21FF6B2C/0", "#00FF6B2C/0.62" },
                wash.Descendants().Where(e => e.Name.LocalName == "GradientStop").Select(g => Attr(g, "Color") + "/" + Attr(g, "Offset")).ToArray());
            var selected = container.Descendants().Single(e => e.Name.LocalName == "Trigger" && Attr(e, "Property") == "IsSelected");
            Assert.Contains(selected.Elements(), s => Attr(s, "TargetName") == "Bar" && Attr(s, "Value") == "Visible");
            Assert.Contains(selected.Elements(), s => Attr(s, "TargetName") == "Wash" && Attr(s, "Value") == "1");

            // A pill is filled ember on a connected row and steel on a row
            // that is not: the device is the controller's, and not in use.
            var pill = template.Descendants().Single(e => (string)e.Attribute(Xaml + "Name") == "Pill");
            Assert.Equal("{DynamicResource EmberBrush}", Attr(pill, "Background"));
            Assert.Equal("{DynamicResource EmberInkBrush}", Attr(pill, "TextElement.Foreground"));
            var away = pill.Parent.Elements().Single(e => e.Name.LocalName == "DataTemplate.Triggers").Elements().Single();
            Assert.Equal("{Binding DataContext.IsOnline, RelativeSource={RelativeSource AncestorType=ItemsControl}}", Attr(away, "Binding"));
            Assert.Equal("False", Attr(away, "Value"));
            Assert.Equal(new[] { "{DynamicResource SteelRaisedBrush}", "{DynamicResource SteelLineBrush}", "{DynamicResource SubRowTextBrush}" },
                away.Elements().Select(s => Attr(s, "Value")).ToArray());

            // Use is an outline, so the filled ember is a device in use.
            Assert.Equal("{StaticResource EmberOutlineButton}", Attr(Named(page, "UseItButton"), "Style"));
            string app = RepoText("PadForge.App", "App.xaml");
            int at = app.IndexOf("<Style x:Key=\"EmberOutlineButton\"", StringComparison.Ordinal);
            Assert.True(at > 0);
            string style = app.Substring(at, app.IndexOf("</Style>", at, StringComparison.Ordinal) - at);
            Assert.Contains("<Setter Property=\"Background\" Value=\"Transparent\"/>", style);
            Assert.Contains("<Setter Property=\"BorderBrush\" Value=\"{DynamicResource EmberRingBrush}\"/>", style);
            Assert.Contains("<Setter Property=\"Foreground\" Value=\"{DynamicResource EmberTextBrush}\"/>", style);
        }

        [Fact]
        public void APartOfTheListHasItsNameAndItsCount()
        {
            var page = Page();
            var group = Keyed(page, "DeviceGroup");
            Assert.Equal("GroupItem", Attr(group, "TargetType"));
            var name = group.Descendants().Single(e => (string)e.Attribute(Xaml + "Name") == "GroupName");
            Assert.Equal("Level2", Attr(name, "AutomationProperties.HeadingLevel"));
            Assert.Equal("{Binding ItemCount}", Attr(name.ElementsAfterSelf().Single(), "Text"));
            // Each part reads and is named by its Show segment's own word.
            var triggers = group.Descendants().Where(e => e.Name.LocalName == "DataTrigger").ToList();
            Assert.Equal(new[] { "{x:Static vm:DeviceFilter.Controllers}", "{x:Static vm:DeviceFilter.KeyboardsMice}" },
                triggers.Select(t => Attr(t, "Value")).ToArray());
            foreach (var (trigger, key) in triggers.Zip(new[] { "Devices_Filter_Controllers", "Devices_Filter_KeyboardsMice" }))
            {
                Assert.Equal("{Binding Name}", Attr(trigger, "Binding"));
                Assert.Equal(new[] { S(key), S(key) }, trigger.Elements().Select(s => Attr(s, "Value")).ToArray());
                Assert.Contains(trigger.Elements(), s => Attr(s, "Property") == "AutomationProperties.Name");
            }
            Assert.Equal(S("Devices_Filter_Other"), Attr(name, "Text"));
            Assert.Contains(group.Elements(), s => Attr(s, "Property") == "AutomationProperties.Name" && Attr(s, "Value") == S("Devices_Filter_Other"));
        }

        [Fact]
        public void ThePanelOpensOnTheDevicesPicture()
        {
            var page = Page();
            var all = page.Descendants().ToList();
            var order = new[] { "IdentityCard", "DeviceStage", "DeviceNameHeading", "DeviceTypeLine", "DeviceTiles", "UsedByChips", "TestCard", "SettingsCard", "DetailsCard" }
                .Select(n => all.IndexOf(Named(page, n))).ToList();
            Assert.Equal(order.OrderBy(i => i), order);

            var stage = Named(page, "DeviceStage");
            Assert.Equal("150", Attr(stage, "Height"));
            // The floor light: a connected device a controller uses.
            var floor = Named(page, "StageFloorLight");
            Assert.Equal(new[] { "{Binding SelectedDevice.IsOnline}", "{Binding SelectedDevice.IsAssigned}" },
                floor.Descendants().Where(e => e.Name.LocalName == "Condition").Select(c => Attr(c, "Binding")).ToArray());
            var picture = Named(page, "StagePicture");
            Assert.Equal("206", Attr(picture, "Width"));
            Assert.Equal("115", Attr(picture, "Height"));
            Assert.Equal("LeftToRight", Attr(picture, "FlowDirection"));
            Assert.Equal("{Binding SelectedPicture}", Attr(picture, "Appear.When"));
            Assert.Equal(new[] { "{Binding SelectedPictureGray}", "{Binding SelectedPicture}" },
                picture.Elements().Where(e => e.Name.LocalName == "DecorativeImage").Select(i => Attr(i, "Source")).ToArray());
            Assert.Equal("{Binding ShowSelectedKind, Converter={StaticResource BoolToVisibilityConverter}}", Attr(Named(page, "StageKind"), "Visibility"));
            Assert.Equal("{Binding SelectedDevice.KindGlyph}", Attr(Named(page, "StageKind"), "Text"));

            var heading = Named(page, "DeviceNameHeading");
            Assert.Equal("20", Attr(heading, "FontSize"));
            Assert.Equal("{StaticResource DisplayFontFamily}", Attr(heading, "FontFamily"));
            Assert.Equal("Level2", Attr(heading, "AutomationProperties.HeadingLevel"));
            Assert.Equal("{Binding SelectedDevice.DeviceType}", Attr(Named(page, "DeviceTypeLine"), "Text"));
            Assert.Same(Named(page, "DeviceTypeLine").Parent, Named(page, "TestRumbleButton").Parent);

            // The tiles: the link and the battery, each where the device
            // has one.
            var tiles = Named(page, "DeviceTiles");
            Assert.Equal("UniformGrid", tiles.Name.LocalName);
            Assert.Equal("2", Attr(tiles, "Columns"));
            Assert.Equal("{Binding SelectedDevice.HasLinkOrBattery, Converter={StaticResource BoolToVisibilityConverter}}", Attr(tiles, "Visibility"));
            Assert.Equal("{Binding SelectedDevice.HasLink, Converter={StaticResource BoolToVisibilityConverter}}", Attr(Named(page, "LinkTile"), "Visibility"));
            var battery = Named(page, "BatteryTile");
            Assert.Equal("{Binding SelectedDevice.HasBattery, Converter={StaticResource BoolToVisibilityConverter}}", Attr(battery, "Visibility"));
            var meter = battery.Descendants().Single(e => e.Name.LocalName == "BatteryMeter");
            Assert.Equal("14", Attr(meter, "Height"));
            Assert.Equal("{Binding SelectedDevice.BatteryLow}", Attr(meter, "Low"));
            Assert.StartsWith("{Binding SelectedDevice.BatterySpoken,", Attr(meter.ElementsAfterSelf().First(), "AutomationProperties.Name"));
            Assert.Contains(battery.Descendants(), e => e.Name.LocalName == "DataTrigger" && Attr(e, "Binding") == "{Binding SelectedDevice.BatteryLow}"
                && e.Elements().Any(s => Attr(s, "Property") == "BorderBrush" && Attr(s, "Value") == "{DynamicResource WaitRingBrush}"));

            // Used By leads its chips on their line.
            var chips = Named(page, "UsedByChips").Descendants().Single(e => e.Name.LocalName == "CompositeCollection").Elements().ToList();
            Assert.Equal(new[] { "ListLabel", "CollectionContainer", "Grid" }, chips.Select(c => c.Name.LocalName).ToArray());
            Assert.Equal(S("Devices_UsedBy"), Attr(chips[0], "Text"));

            // The page's gutters are the board's.
            Assert.Equal("36,26,36,0", Attr(Named(page, "HeaderRow"), "Margin"));
            Assert.Equal("36,16,36,16", Attr(Named(page, "FilterRow"), "Margin"));
            Assert.Equal((DevicesPage.PanelWidth + DevicesPage.PageSide).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Attr(Named(page, "PanelColumn"), "Width"));
            Assert.Equal("28", Attr(Named(page, "DevicesTitle"), "FontSize"));
        }

        [Fact]
        public void TheCompactTestShowsTheTriggersInNumbersAndAChipForEachButton()
        {
            var page = Page();
            var test = Named(page, "GamepadTest");
            foreach (string trigger in new[] { "TestLeftTrigger", "TestRightTrigger" })
            {
                var bar = test.Descendants().Single(e => e.Name.LocalName == "ProgressBar" && Attr(e, "Value") == "{Binding " + trigger + ", Mode=OneWay}");
                var number = bar.ElementsAfterSelf().Single();
                Assert.Equal("{Binding " + trigger + ", StringFormat={}{0:P0}}", Attr(number, "Text"));
                // Cold blue while the trigger is pulled at all.
                var rest = number.Descendants().Single(e => e.Name.LocalName == "DataTrigger");
                Assert.Equal("{Binding " + trigger + "}", Attr(rest, "Binding"));
                Assert.Equal("0", Attr(rest, "Value"));
            }
            var line = Named(page, "PressedLine");
            var parts = line.Elements().ToList();
            Assert.Equal("{Binding TestPressedLead}", Attr(parts[0], "Text"));
            Assert.Equal("{Binding TestPressed}", Attr(parts[1], "ItemsSource"));
            Assert.Equal("{Binding NothingPressed, Converter={StaticResource BoolToVisibilityConverter}}", Attr(parts[2], "Visibility"));
            Assert.Equal(S("Common_None"), Attr(parts[2], "Text"));
            Assert.Equal("{Binding TestPressedTail}", Attr(parts[3], "Text"));
        }

        /// <summary>A fold's line is 48 tall with its chevron 12 from its
        /// title on every page, unless a page sets the two: the Devices
        /// page's folds stand in its table.</summary>
        [Fact]
        public void AFoldsLineKeepsItsFormUnlessAPageSetsIt() => RunSta(() =>
        {
            var fold = new FoldCard();
            Assert.Equal(48.0, fold.HeaderMinHeight);
            Assert.Equal(new Thickness(0, 0, 12, 0), fold.ChevronMargin);
            string icons = RepoText("PadForge.App", "Resources", "ControllerIcons.xaml");
            Assert.Contains("MinHeight=\"{TemplateBinding HeaderMinHeight}\" Padding=\"{TemplateBinding Padding}\"", icons);
            Assert.Contains("VerticalAlignment=\"Center\" Margin=\"{TemplateBinding ChevronMargin}\"/>", icons);
        });
    }
}
