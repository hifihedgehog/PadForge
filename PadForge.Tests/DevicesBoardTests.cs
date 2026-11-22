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

        /// <summary>The Details card and Copy Details name the link the
        /// panel's Link tile names: Bluetooth, USB, PS/2 or I2C.</summary>
        [Fact]
        public void TheDetailsCardAndCopyDetailsNameEveryLink()
        {
            var page = Page();
            var details = page.Descendants().Where(e => e.Name.LocalName == "DockPanel"
                && e.Elements().Any(k => Attr(k, "Text") == S("Devices_Detail_Link"))).ToList();
            Assert.NotEmpty(details);
            foreach (var row in details)
                Assert.Contains("SelectedDevice.HasLink", Attr(row, "Visibility"));
            Assert.DoesNotContain("IsBluetoothLink", RepoText("PadForge.App", "Views", "DevicesPage.xaml"));
            Assert.Contains("SelectedDevice.LinkText", RepoText("PadForge.App", "Views", "DevicesPage.xaml"));

            Assert.Equal("BT", DevicesPage.DossierLink(DeviceLink.Bluetooth));
            Assert.Equal("USB", DevicesPage.DossierLink(DeviceLink.Usb));
            Assert.Equal("PS/2", DevicesPage.DossierLink(DeviceLink.Ps2));
            Assert.Equal("I2C", DevicesPage.DossierLink(DeviceLink.I2c));
            Assert.Null(DevicesPage.DossierLink(DeviceLink.None));
            string code = RepoText("PadForge.App", "Views", "DevicesPage.xaml.cs");
            Assert.Contains("DossierLink(dev.Link)", code);
            Assert.DoesNotContain("if (dev.IsBluetoothLink)", code);
        }

        /// <summary>A link read while the device was away, or from a walk that
        /// ended before it settled, is read again once the row is online.</summary>
        [Fact]
        public void ARowComingOnlineReadsItsLinkAgain()
        {
            var walk = DeviceTransport.TreeWalk;
            var tree = new List<PadForge.Engine.AncestryHop>();
            int walks = 0;
            DeviceTransport.ForgetLinks();
            DeviceTransport.TreeWalk = path => { walks++; return tree.ToArray(); };
            try
            {
                var row = Row("DualSense Wireless Controller", "Gamepad", DualSenseUsb, false, 0x054C, 0x0CE6);
                Assert.False(row.HasLink);
                Assert.Equal(1, walks);
                Assert.False(row.HasLink);
                Assert.Equal(1, walks);

                tree.Add(new PadForge.Engine.AncestryHop(@"HID\VID_054C&PID_0CE6&MI_03\8&1A2B3C4D&0&0000", Array.Empty<string>(), "HidUsb"));
                tree.Add(new PadForge.Engine.AncestryHop(@"USB\VID_054C&PID_0CE6\7&1&0&4", Array.Empty<string>(), "HidUsb"));
                tree.Add(new PadForge.Engine.AncestryHop(@"PCI\VEN_1022&DEV_15B7\4&1&0&0441", Array.Empty<string>(), "USBXHCI"));
                row.IsOnline = true;
                Assert.True(row.HasLink);
                Assert.Equal(DeviceLink.Usb, row.Link);
            }
            finally
            {
                DeviceTransport.TreeWalk = walk;
                DeviceTransport.ForgetLinks();
            }
        }

        [Fact]
        public void ARowKnowsItsPartItsLinkAndWhetherItsBatteryIsLow()
        {
            var s = Strings.Instance;
            var pad = Row("DualSense Wireless Controller", "Gamepad", DualSenseBluetooth, true, 0x054C, 0x0CE6);
            Assert.Equal(DeviceFilter.Gamepad, pad.Section);
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
            Assert.Equal(DeviceFilter.Mouse, mouse.Section);
            Assert.Equal(DeviceFilter.Other, Row("Microphone", "Microphone", "mic://capture/0", true).Section);

            // A wheel and a flight stick have parts of their own, and a
            // Falcon stands in the Other part.
            var wheel = Row("G29", "Wheel", @"\\?\HID#VID_046D&PID_C24F#8&6&0&0000", true, 0x046D, 0xC24F);
            Assert.Equal(DeviceFilter.Wheel, wheel.Section);
            var stick = Row("T.16000M", "FlightStick", @"\\?\HID#VID_044F&PID_B10A#8&7&0&0000", true, 0x044F, 0xB10A);
            Assert.Equal(DeviceFilter.Joystick, stick.Section);
            Assert.Equal(DeviceFilter.Other, Row("Falcon", "NovintFalcon", "falcon://0", true).Section);

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

            Assert.Equal(new[] { "Gamepad: B Pad, E Pad", "Keyboard: A Keyboard", "Mouse: D Mouse", "Other: C Midi" }, Parts(vm.MainView));
            Assert.Equal(new[] { padB, padE, keyboard, mouse, midi }, vm.MainView.Cast<DeviceRowViewModel>().ToArray());
            Assert.Equal(vm.RowsOf(DevicePageList.Main, inParts: true), vm.MainView.Cast<DeviceRowViewModel>());
            // The folded list has no parts.
            Assert.Null(vm.NotConnectedView.Groups);

            // A part's rows all go: the part goes with them.
            padB.IsOnline = false;
            padE.IsOnline = false;
            vm.RefreshCounts();
            Assert.Equal(new[] { "Keyboard: A Keyboard", "Mouse: D Mouse", "Other: C Midi" }, Parts(vm.MainView));
            // And comes back first, though it is the last to arrive.
            padE.IsOnline = true;
            vm.RefreshCounts();
            Assert.Equal(new[] { "Gamepad: E Pad", "Keyboard: A Keyboard", "Mouse: D Mouse", "Other: C Midi" }, Parts(vm.MainView));

            // The segment leaves one part.
            vm.Filter = DeviceFilter.Mouse;
            Assert.Equal(new[] { "Mouse: D Mouse" }, Parts(vm.MainView));
            vm.Filter = DeviceFilter.All;

            // A row whose type changes moves to its new part.
            mouse.DeviceTypeKey = "Midi";
            vm.RefreshCounts();
            Assert.Equal(new[] { "Gamepad: E Pad", "Keyboard: A Keyboard", "Other: C Midi, D Mouse" }, Parts(vm.MainView));

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
            Assert.Equal(new[] { "Gamepad: X Pad", "Keyboard: Y Keyboard" }, Parts(two.MainView));
            second.DeviceTypeKey = "Gamepad";
            Assert.Equal(new[] { first, second }, two.MainView.Cast<DeviceRowViewModel>().ToArray());
            Assert.Equal(new[] { first, second }, two.RowsOf(DevicePageList.Main, inParts: true).ToArray());
            two.RefreshCounts();
            Assert.Equal(new[] { "Gamepad: X Pad, Y Keyboard" }, Parts(two.MainView));
        });

        /// <summary>Every kind the page sorted devices by before PadForge 6
        /// has its part again, in the segments' order: a wheel beside a
        /// flight stick, a keyboard apart from a mouse. Three parts stood
        /// for all of them, Controllers, Keyboards &amp; Mice and Other
        /// (owner, 2026-11-17).</summary>
        [Fact]
        public void EachKindOfDeviceHasItsOwnPart() => RunSta(() =>
        {
            var vm = new DevicesViewModel();
            foreach (var row in new[]
                     {
                         Row("A Wheel", "Wheel", @"\\?\HID#VID_046D&PID_C24F#8&6&0&0000", true, 0x046D, 0xC24F),
                         Row("B Flight Stick", "FlightStick", @"\\?\HID#VID_044F&PID_B10A#8&7&0&0000", true, 0x044F, 0xB10A),
                         Row("C Pad", "Gamepad", DualSenseUsb, true, 0x054C, 0x0CE6),
                         Row("D Mouse", "Mouse", MousePath, true, 0x046D, 0xC08B),
                         Row("E Keyboard", "Keyboard", KeyboardPath, true, 0x046D, 0xC33F),
                         Row("F Joystick", "Joystick", @"\\?\HID#VID_046D&PID_C215#8&8&0&0000", true, 0x046D, 0xC215),
                         Row("G Midi", "Midi", "midi://in/0", true),
                         Row("H Analog Keyboard", "AnalogKeyboard", "analogkb://0", true),
                     })
                vm.Devices.Add(row);
            vm.RefreshCounts();
            vm.EnsureListViews();
            Assert.Equal(new[]
            {
                "Gamepad: C Pad", "Joystick: B Flight Stick, F Joystick", "Wheel: A Wheel",
                "Keyboard: E Keyboard, H Analog Keyboard", "Mouse: D Mouse", "Other: G Midi",
            }, Parts(vm.MainView));

            vm.Filter = DeviceFilter.Wheel;
            Assert.Equal(new[] { "Wheel: A Wheel" }, Parts(vm.MainView));
            vm.Filter = DeviceFilter.Joystick;
            Assert.Equal(new[] { "Joystick: B Flight Stick, F Joystick" }, Parts(vm.MainView));
        });

        /// <summary>The connection segments pick what the main list shows:
        /// every device, with the connected ones in the list and the rest in
        /// the Not Connected fold, the connected ones alone, or the ones
        /// that are not connected, in the list in parts by kind (owner,
        /// 2026-11-17). The count under the title stays the connected
        /// devices, whatever the segment.</summary>
        [Fact]
        public void TheConnectionSegmentsPickWhatTheListShows() => RunSta(() =>
        {
            var vm = new DevicesViewModel();
            var pad = Row("A Pad", "Gamepad", DualSenseUsb, true, 0x054C, 0x0CE6);
            var wheel = Row("B Wheel", "Wheel", @"\\?\HID#VID_046D&PID_C24F#8&6&0&0000", false, 0x046D, 0xC24F);
            var keyboard = Row("C Keyboard", "Keyboard", KeyboardPath, true, 0x046D, 0xC33F);
            var mouse = Row("D Mouse", "Mouse", MousePath, false, 0x046D, 0xC08B);
            var media = Row("E Media Keys", "ConsumerControl", @"\\?\HID#VID_046D&PID_C33F&MI_01&Col02#8&9&0&0001", true, 0x046D, 0xC33F);
            foreach (var row in new[] { pad, wheel, keyboard, mouse, media }) vm.Devices.Add(row);
            vm.RefreshCounts();
            vm.EnsureListViews();
            static DeviceRowViewModel[] Rows(System.ComponentModel.ICollectionView view) => view.Cast<DeviceRowViewModel>().ToArray();

            // The keyboard's media keys stand in the Other part, and the
            // count under the title leaves them out: they are the keyboard's.
            Assert.Equal(new[] { "Gamepad: A Pad", "Keyboard: C Keyboard", "Other: E Media Keys" }, Parts(vm.MainView));
            Assert.Equal(new[] { wheel, mouse }, Rows(vm.NotConnectedView));
            Assert.Equal(2, vm.ConnectedTotal);

            vm.Connection = DeviceConnection.Connected;
            Assert.Equal(new[] { "Gamepad: A Pad", "Keyboard: C Keyboard", "Other: E Media Keys" }, Parts(vm.MainView));
            Assert.Empty(Rows(vm.NotConnectedView));
            Assert.False(vm.HasNotConnected);

            vm.Connection = DeviceConnection.NotConnected;
            Assert.Equal(new[] { "Wheel: B Wheel", "Mouse: D Mouse" }, Parts(vm.MainView));
            Assert.Equal(vm.RowsOf(DevicePageList.Main, inParts: true), Rows(vm.MainView));
            Assert.Equal(2, vm.Devices.Count(d => vm.Shows(DevicePageList.Main, d)));
            Assert.Empty(Rows(vm.NotConnectedView));
            Assert.False(vm.HasNotConnected);
            Assert.Equal(2, vm.ConnectedTotal);

            // The segments and the search narrow one another.
            vm.Filter = DeviceFilter.Gamepad;
            Assert.Empty(Rows(vm.MainView));
            Assert.True(vm.ShowNoMatch);
            vm.Filter = DeviceFilter.All;
            vm.Connection = DeviceConnection.All;
            Assert.False(vm.ShowNoMatch);
            Assert.Equal(new[] { wheel, mouse }, Rows(vm.NotConnectedView));
        });

        /// <summary>The connection segments share the search box's line
        /// when it holds them beside a box of 220, and go to the next line
        /// when it does not.</summary>
        [Fact]
        public void TheConnectionSegmentsShareTheSearchLineWhenItHasRoom()
        {
            Assert.Equal(540 + 16 + 260, DevicesPage.SegmentsWidth(1052, 540, 260));
            Assert.Equal(540, DevicesPage.SegmentsWidth(1051, 540, 260));
            Assert.Equal(220, DevicesPage.SearchWidth(1052, DevicesPage.SegmentsWidth(1052, 540, 260)));
            Assert.Equal(320, DevicesPage.SearchWidth(1200, DevicesPage.SegmentsWidth(1200, 540, 260)));
            Assert.Equal(320, DevicesPage.SearchWidth(1051, DevicesPage.SegmentsWidth(1051, 540, 260)));
        }

        /// <summary>Every link a device can have shows its mark beside its
        /// word, in the list and on the device's Link tile: Bluetooth's
        /// rune, USB's trident, the I2C bus logo and the PS/2 socket's face
        /// (owner, 2026-11-17).</summary>
        [Fact]
        public void EveryLinkHasItsMark()
        {
            var page = Page();
            string shell = RepoText("PadForge.App", "Resources", "Shell.xaml");
            var marks = new Dictionary<string, string>
            {
                ["Bluetooth"] = "BluetoothLineGeometry", ["Usb"] = "UsbLineGeometry",
                ["I2c"] = "I2cLineGeometry", ["Ps2"] = "Ps2LineGeometry",
            };
            Assert.Equal(Enum.GetNames<PadForge.Common.DeviceLink>().Where(n => n != "None").OrderBy(n => n), marks.Keys.OrderBy(n => n));
            foreach (string key in marks.Values) Assert.Contains("<Geometry x:Key=\"" + key + "\">", shell);

            void Drawn(IEnumerable<XElement> triggers, string binding, string target)
            {
                var list = triggers.ToList();
                Assert.Equal(marks.Count, list.Count);
                foreach (var trigger in list)
                {
                    Assert.Equal(binding, Attr(trigger, "Binding"));
                    string link = Attr(trigger, "Value")["{x:Static common:DeviceLink.".Length..^1];
                    var setter = trigger.Elements().Single();
                    Assert.Equal(target, Attr(setter, "TargetName"));
                    Assert.Equal("Data", Attr(setter, "Property"));
                    Assert.Equal("{StaticResource " + marks[link] + "}", Attr(setter, "Value"));
                }
            }
            Drawn(Keyed(page, "DeviceLinkFact").Descendants().Where(e => e.Name.LocalName == "DataTrigger"), "{Binding Link}", "Mark");
            Drawn(Named(page, "StageLinkMark").Descendants().Where(e => e.Name.LocalName == "DataTrigger"), "{Binding SelectedDevice.Link}", null);
        }

        /// <summary>A merged row stands with the devices it merges: in its
        /// type's part of the connected list, after them. Under Other
        /// Inputs, a person who chose a kind saw every device of it but the
        /// merged one (owner, 2026-11-17). It is a row and no device, so
        /// the count of connected devices leaves it out.</summary>
        [Fact]
        public void AMergedRowStandsInItsTypesPart() => RunSta(() =>
        {
            var vm = new DevicesViewModel();
            var keyboard = Row("A Keyboard", "Keyboard", KeyboardPath, true, 0x046D, 0xC33F);
            var mouse = Row("B Mouse", "Mouse", MousePath, true, 0x046D, 0xC08B);
            var pad = Row("C Pad", "Gamepad", DualSenseUsb, true, 0x054C, 0x0CE6);
            var keyboards = Row("All Keyboards (Merged)", "Keyboard", "aggregate://keyboards", true);
            var mice = Row("All Mice (Merged)", "Mouse", "aggregate://mice", true);
            var touchpads = Row("All Touchpads (Merged)", "Touchpad", "aggregate://touchpads", true);
            var controls = Row("All Consumer Controls (Merged)", "ConsumerControl", "aggregate://consumercontrols", true);
            foreach (var row in new[] { keyboard, mouse, pad, keyboards, mice, touchpads, controls }) vm.Devices.Add(row);
            vm.RefreshCounts();
            vm.EnsureListViews();

            // The merged consumer controls stand in the Other part, where the
            // consumer controls stand.
            Assert.Equal(new[]
            {
                "Gamepad: C Pad", "Keyboard: A Keyboard, All Keyboards (Merged)", "Mouse: B Mouse, All Mice (Merged)",
                "Other: All Touchpads (Merged), All Consumer Controls (Merged)",
            }, Parts(vm.MainView));
            Assert.Equal(3, vm.ConnectedTotal);
            Assert.Equal(7, vm.Devices.Count(d => vm.Shows(DevicePageList.Main, d)));

            vm.Filter = DeviceFilter.Keyboard;
            Assert.Equal(new[] { "Keyboard: A Keyboard, All Keyboards (Merged)" }, Parts(vm.MainView));
            vm.Filter = DeviceFilter.Mouse;
            Assert.Equal(new[] { "Mouse: B Mouse, All Mice (Merged)" }, Parts(vm.MainView));

            // With no device of its kind connected, a merged row is not
            // connected either.
            vm.Filter = DeviceFilter.All;
            mice.IsOnline = false;
            vm.RefreshCounts();
            Assert.Contains(mice, vm.NotConnectedView.Cast<DeviceRowViewModel>());
            Assert.DoesNotContain(mice, vm.MainView.Cast<DeviceRowViewModel>());
        });

        [Fact]
        public void ThePartsAreOrderedByTheShowSegments()
        {
            var order = DevicesViewModel.SectionOrder.Instance;
            Assert.Equal(new[]
                {
                    DeviceFilter.All, DeviceFilter.Gamepad, DeviceFilter.Joystick, DeviceFilter.Wheel,
                    DeviceFilter.Keyboard, DeviceFilter.Mouse, DeviceFilter.Other,
                },
                Enum.GetValues<DeviceFilter>());
            // The list's parts are every segment but All, in the same order.
            Assert.Equal(Enum.GetValues<DeviceFilter>().Skip(1), DevicesViewModel.Parts);
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
            vm.Filter = DeviceFilter.Keyboard;
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

        /// <summary>A controller on another link, or through its own
        /// dongle, is still that controller. A preset is one device on one
        /// link, so the Steam Controllers on Bluetooth and through their
        /// dongles answered to no preset and had no picture, where a Sony
        /// or a Microsoft pad had one on either link (owner, 2026-11-14).
        /// Each such id names the preset whose body is its own. The ids are
        /// SDL's (src/joystick/controller_list.h): the 2015 Steam Controller
        /// as 1101, 1105, 1106 and 1142 beside the preset's 1102, the 2026
        /// one as 1303, 1304 and 1305 beside 1302, a DualShock 4 through
        /// Sony's wireless adaptor, an Xbox One S pad on Bluetooth with its
        /// first firmware and its Bluetooth LE one, and the Adaptive
        /// Controller on both.</summary>
        [Fact]
        public void AControllerOnAnotherLink_HasItsOwnPicture()
        {
            foreach (ushort product in new ushort[] { 0x1101, 0x1102, 0x1105, 0x1106, 0x1142 })
                Assert.Equal(("STEAMCONTROLLER", "SteamController"), StagePictures.ForDevice(0x28DE, product));
            foreach (ushort product in new ushort[] { 0x1302, 0x1303, 0x1304, 0x1305 })
                Assert.Equal(("STEAMCONTROLLER2", "SteamController2"), StagePictures.ForDevice(0x28DE, product));
            Assert.Equal(("STEAMDECK", "SteamDeck"), StagePictures.ForDevice(0x28DE, 0x1205));
            Assert.Equal(("DS4", "DS4"), StagePictures.ForDevice(0x054C, 0x0BA0));
            Assert.Equal(("XBOXONE", "XboxSeries"), StagePictures.ForDevice(0x045E, 0x02E0));
            Assert.Equal(("XBOXONE", "XboxSeries"), StagePictures.ForDevice(0x045E, 0x0B20));
            Assert.Equal(("XBOXONE", "XboxSeries"), StagePictures.ForDevice(0x045E, 0x0B0C));
            Assert.Equal(("XBOXONE", "XboxSeries"), StagePictures.ForDevice(0x045E, 0x0B21));
            // Valve's unreleased second pad and Steam's own virtual gamepad
            // are no controller PadForge has a body for.
            Assert.Null(StagePictures.ForDevice(0x28DE, 0x1201).Folder);
            Assert.Null(StagePictures.ForDevice(0x28DE, 0x11FF).Folder);

            // Each id in the table names a preset that is in the catalog and
            // has art of its own, and no preset has the id itself: the
            // table holds only what the catalog cannot answer.
            var presets = PadForge.Common.Input.HMaestroProfileCatalog.AllProfiles.ToList();
            Assert.Equal(12, StagePictures.SameController.Count);
            foreach (var (ids, presetId) in StagePictures.SameController)
            {
                var preset = presets.SingleOrDefault(p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));
                Assert.True(preset != null, presetId + " is in the catalog");
                PadForge.Common.Input.HMaestroProfileCatalog.ResolveAssetFolders(presetId, VirtualControllerType.Xbox, out bool dedicated);
                Assert.True(dedicated, presetId + " has art of its own");
                Assert.Equal(ids.Vendor, preset.VendorId);
                Assert.DoesNotContain(presets, p => p.VendorId == ids.Vendor && p.ProductId == ids.Product);
            }
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
            Assert.Contains("RefreshPictures();", service.Substring(made, 200));
            int pictures = service.IndexOf("internal void RefreshPictures()", StringComparison.Ordinal);
            string onMade = service.Substring(pictures, 400);
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
        public void TheListShowsLinkAndBatteryAsColumnsFromTableMin() => RunSta(() =>
        {
            Assert.False(DevicesPage.IsWide(DevicesPage.TableMin - 0.5));
            Assert.True(DevicesPage.IsWide(DevicesPage.TableMin));
            // The room the columns and the row's insets take, and what is
            // then left a name at the least width. The Remove column is the
            // button's width and its margin, read from the page.
            var page = Page();
            var removeButton = Named(page, "RowRemoveButton");
            Assert.Equal("8,0,0,0", Attr(removeButton, "Margin"));
            var removeStyle = Keyed(page, "DeviceRowRemoveButton");
            double remove = 8 + double.Parse(removeStyle.Elements()
                .Single(e => Attr(e, "Property") == "Width").Attribute("Value").Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(36, remove);
            const double insets = 18 + 16, tile = 40, gap = 14, link = 104, battery = 108, usedBy = 104;
            Assert.Equal(DevicesPage.TableMin - 214, insets + tile + gap + (gap + link) + (gap + battery) + (gap + usedBy) + remove);

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
            Assert.Equal(new[] { "40/", "*/", "Auto/DeviceLink", "Auto/DeviceBattery", "Auto/DeviceUsedBy", "Auto/DeviceRemove" }, Columns(head));
            Assert.Equal(Columns(head), Columns(rowGrid));
            Assert.Equal("18,12,16,10", Attr(head, "Margin"));
            Assert.Equal("18,0,16,0", Attr(row, "Padding"));

            var labels = head.Elements().Where(e => e.Name.LocalName == "TextBlock").ToList();
            Assert.Equal(new[] { null, S("Devices_Detail_Link"), S("Devices_Detail_Battery"), S("Devices_UsedBy") },
                labels.Select(l => Attr(l, "Text")).ToArray());
            // The main list's name follows the connection segment: Connected,
            // or Not Connected under that segment, and a screen reader hears
            // the list by it.
            var heading = Named(page, "MainHeading");
            Assert.Same(labels[0], heading);
            var headingStyle = heading.Descendants().Single(e => e.Name.LocalName == "Style");
            Assert.Equal(S("Devices_Group_Connected"), headingStyle.Elements().Where(e => e.Name.LocalName == "Setter")
                .Single(s => Attr(s, "Property") == "Text").Attribute("Value").Value);
            var notConnected = headingStyle.Descendants().Single(e => e.Name.LocalName == "DataTrigger");
            Assert.Equal("{Binding Connection}", Attr(notConnected, "Binding"));
            Assert.Equal("{x:Static vm:DeviceConnection.NotConnected}", Attr(notConnected, "Value"));
            Assert.Equal(S("Devices_Group_NotConnected"), Attr(notConnected.Elements().Single(), "Value"));
            Assert.Equal("{Binding Text, ElementName=MainHeading, TargetNullValue='', FallbackValue=''}",
                Attr(Named(page, "MainList"), "AutomationProperties.Name"));
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
            // devices in their parts, then the fold. No other fold stands
            // beside it: the inputs an Other Inputs fold held stand in their
            // kinds' parts (owner, 2026-11-17).
            var all = page.Descendants().ToList();
            Assert.Null(page.Descendants().FirstOrDefault(e => (string)e.Attribute(Xaml + "Name") == "OtherInputsFold"));
            var order = new[] { "ListHead", "NoMatchLine", "MainList", "NotConnectedFold" }
                .Select(n => all.IndexOf(Named(page, n))).ToList();
            Assert.Equal(order.OrderBy(i => i), order);
            var groupStyle = Named(page, "MainList").Descendants().Single(e => e.Name.LocalName == "GroupStyle");
            Assert.Equal("{StaticResource DeviceGroup}", Attr(groupStyle, "ContainerStyle"));
            Assert.DoesNotContain(Named(page, "NotConnectedList").Descendants(), e => e.Name.LocalName == "GroupStyle");

            // A fold's chevron stands under the rows' tiles and its title
            // under their names.
            foreach (string fold in new[] { "NotConnectedFold" })
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
            Assert.DoesNotContain("MainShown", text);
            Assert.DoesNotContain("{Binding Devices_NotInUse", text);
        }

        [Fact]
        public void EveryRowIsOneFormWithItsTypeUnderItsNameAndLitWhileItSends()
        {
            var page = Page();
            var template = Keyed(page, "DeviceRowTemplate");
            Assert.Equal("63", Attr(Named(page, "Row"), "MinHeight"));
            var triggers = template.Elements().Single(e => e.Name.LocalName == "DataTemplate.Triggers").Elements().ToList();
            Dictionary<string, string> Setters(XElement trigger) => trigger.Elements()
                .ToDictionary(s => Attr(s, "TargetName") + "." + Attr(s, "Property"), s => Attr(s, "Value"));

            // One form for every kind (owner, 2026-11-17): a keyboard's row
            // and a mouse's read as a controller's. The name stands over
            // the type once, no trigger changes the row's height or shows
            // one form of the name in place of another, and no row is asked
            // whether it is a controller's.
            var words = Named(page, "NameAndType");
            Assert.Equal("StackPanel", words.Name.LocalName);
            Assert.Null(Attr(words, "Orientation"));
            Assert.Null(Attr(words, "Visibility"));
            Assert.Equal("1", Attr(words, "Grid.Column"));
            var lines = words.Elements().ToList();
            Assert.Equal(2, lines.Count);
            Assert.Same(Named(page, "RowName"), lines[0]);
            Assert.Equal(("{Binding DeviceName}", "SemiBold", "CharacterEllipsis"),
                (Attr(lines[0], "Text"), Attr(lines[0], "FontWeight"), Attr(lines[0], "TextTrimming")));
            Assert.Equal(("StackPanel", "Horizontal"), (lines[1].Name.LocalName, Attr(lines[1], "Orientation")));
            Assert.Same(Named(page, "RowType"), lines[1].Elements().First());
            Assert.Equal(("{Binding DeviceType}", "{StaticResource DeviceSmall}"),
                (Attr(Named(page, "RowType"), "Text"), Attr(Named(page, "RowType"), "Style")));
            Assert.Single(template.Descendants(), e => Attr(e, "Text") == "{Binding DeviceName}");
            Assert.Single(template.Descendants(), e => Attr(e, "Text") == "{Binding DeviceType}");
            Assert.DoesNotContain(triggers.SelectMany(t => t.Elements()), s => Attr(s, "Property") is "MinHeight" or "Height"
                || (Attr(s, "TargetName") is "NameAndType" or "RowName" or "RowType" && Attr(s, "Property") == "Visibility"));
            Assert.DoesNotContain("IsControllerRow", RepoText("PadForge.App", "Views", "DevicesPage.xaml"));
            Assert.Null(typeof(DeviceRowViewModel).GetProperty("IsControllerRow"));
            // Every row's tile is one size (owner, 2026-11-17): no trigger
            // sizes the tile or its kind.
            var tile = Named(page, "Tile");
            Assert.Equal(("36", "36", "9"), (Attr(tile, "Width"), Attr(tile, "Height"), Attr(tile, "CornerRadius")));
            Assert.Equal("18", Attr(Named(page, "Glyph"), "FontSize"));
            Assert.DoesNotContain(triggers.SelectMany(t => t.Elements()), s => Attr(s, "TargetName") is "Tile" or "Glyph"
                && Attr(s, "Property") is "Width" or "Height" or "CornerRadius" or "FontSize");

            // The tile's light is the device's input, and ember is for that
            // alone: the picked row's glyph is the text's own color.
            var halo = Named(page, "Tile").Parent;
            Assert.Equal("Halo", halo.Name.LocalName);
            Assert.Equal("{Binding IsSending}", Attr(halo, "IsLit"));
            Assert.Equal("19.5", Attr(halo, "Radius"));
            Assert.Equal("0.2", Attr(halo, "Strength"));
            var sending = Setters(triggers.Single(t => Attr(t, "Binding") == "{Binding IsSending}"));
            Assert.Equal("{DynamicResource EmberRingBrush}", sending["Tile.BorderBrush"]);
            Assert.Equal("{DynamicResource EmberHotBrush}", sending["Glyph.Foreground"]);
            var picked = Setters(triggers.Single(t => (Attr(t, "Binding") ?? string.Empty).StartsWith("{Binding IsSelected,", StringComparison.Ordinal)));
            Assert.Equal("{DynamicResource TextFillColorPrimaryBrush}", picked["Glyph.Foreground"]);

            // A name runs on into the columns its row has nothing in.
            var noLink = Setters(triggers.Single(t => Attr(t, "Binding") == "{Binding HasLink}"));
            Assert.Equal(new Dictionary<string, string> { ["NameAndType.Grid.ColumnSpan"] = "2" }, noLink);
            var neither = Setters(triggers.Single(t => Attr(t, "Binding") == "{Binding HasLinkOrBattery}"));
            Assert.Equal(new Dictionary<string, string> { ["NameAndType.Grid.ColumnSpan"] = "3" }, neither);
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
                ["FactsAfterType.Visibility"] = "Visible",
            }, Setters(narrow));
            var facts = Named(page, "FactsAfterType");
            Assert.Same(lines[1], facts.Parent);
            Assert.Equal("Collapsed", Attr(facts, "Visibility"));
            Assert.Equal(new[] { "{StaticResource DeviceLinkFact}", "{StaticResource DeviceBatteryFact}" },
                facts.Elements().Select(e => Attr(e, "ContentTemplate")).ToArray());
            Assert.Equal("{StaticResource DeviceLinkFact}", Attr(Named(page, "LinkCell"), "ContentTemplate"));
            Assert.Equal("{StaticResource DeviceBatteryFact}", Attr(Named(page, "BatteryCell"), "ContentTemplate"));

            // The picked row: an ember bar at its start and a wash.
            var container = Keyed(page, "DeviceRowContainer");
            var bar = container.Descendants().Single(e => (string)e.Attribute(Xaml + "Name") == "Bar");
            Assert.Equal("3", Attr(bar, "Width"));
            Assert.Equal("{DynamicResource EmberBrush}", Attr(bar, "Background"));
            var wash = container.Descendants().Single(e => (string)e.Attribute(Xaml + "Name") == "Wash");
            Assert.Equal(new[] { "{DynamicResource Ember21Color}/0", "{DynamicResource Ember00Color}/0.62" },
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
            Assert.Equal(new[] { "Gamepad", "Joystick", "Wheel", "Keyboard", "Mouse" }.Select(k => "{x:Static vm:DeviceFilter." + k + "}"),
                triggers.Select(t => Attr(t, "Value")).ToArray());
            // A kind's name is the word its rows carry as their type.
            foreach (var (trigger, key) in triggers.Zip(new[]
                     { "DeviceType_Gamepad", "DeviceType_Joystick", "DeviceType_Wheel", "DeviceType_Keyboard", "DeviceType_Mouse" }))
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
            Assert.Equal("{Binding SelectedDevice.KindGlyph}", Attr(Named(page, "StageKind"), "Glyph"));
            Assert.Equal("{Binding SelectedDevice.KindShape}", Attr(Named(page, "StageKind"), "Shape"));

            var heading = Named(page, "DeviceNameHeading");
            Assert.Equal("20", Attr(heading, "FontSize"));
            Assert.Equal("{DynamicResource DisplayFontFamily}", Attr(heading, "FontFamily"));
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
