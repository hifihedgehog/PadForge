using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;
using System.Xml.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Devices page (#574): its three lists and what puts a row in each,
    /// the Show segments and the search, the counts and the folded lines'
    /// names, a row's line, its pills and its Use It, the panel's cards and
    /// Reset All, the compact test, the page's wrap, and the page's parts.
    /// The class touches the slot statics, so it runs with the other tests
    /// that do.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class DevicesPageTests
    {
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        private const string DualSenseUsb = @"\\?\HID#VID_054C&PID_0CE6&MI_03#8&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private const string DualSenseBluetooth = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0ce6#9&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

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

        private static string Attr(XElement e, string name)
            => e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

        /// <summary>WPF views need an STA thread.</summary>
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
            Assert.True(thread.Join(30000), "the Devices page test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static DeviceRowViewModel Row(string name, string typeKey, string path, bool online,
            ushort vid = 0, ushort pid = 0, string product = null)
            => new DeviceRowViewModel
            {
                InstanceGuid = Guid.NewGuid(), DeviceName = name, ProductName = product ?? name,
                DeviceTypeKey = typeKey, DevicePath = path, VendorId = vid, ProductId = pid, IsOnline = online,
            };

        /// <summary>The board's ten rows: five connected, two remembered and
        /// three other inputs.</summary>
        private static DevicesViewModel BoardSet()
        {
            var vm = new DevicesViewModel();
            vm.Devices.Add(Row("DualSense Wireless Controller", "Gamepad", DualSenseBluetooth, true, 0x054C, 0x0CE6));
            vm.Devices.Add(Row("Xbox Series X GIP Controller", "Gamepad", @"\\?\HID#VID_045E&PID_0B12&IG_00#8&2&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", true, 0x045E, 0x0B12));
            vm.Devices.Add(Row("Nintendo Switch Pro Controller", "Gamepad", @"\\?\HID#VID_057E&PID_2009#8&3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", true, 0x057E, 0x2009));
            vm.Devices.Add(Row("Logitech G502 HERO Gaming Mouse", "Mouse", @"\\?\HID#VID_046D&PID_C08B&MI_00#8&4&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}", true, 0x046D, 0xC08B));
            vm.Devices.Add(Row("HID Keyboard Device", "Keyboard", @"\\?\HID#VID_046D&PID_C33F&MI_00#8&5&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}", true, 0x046D, 0xC33F));
            vm.Devices.Add(Row("Logitech G29 Driving Force Racing Wheel", "Wheel", @"\\?\HID#VID_046D&PID_C24F#8&6&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", false, 0x046D, 0xC24F));
            vm.Devices.Add(Row("DualShock 3", "Gamepad", @"\\?\HID#VID_054C&PID_0268#8&7&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", false, 0x054C, 0x0268));
            vm.Devices.Add(Row("All Keyboards (Merged)", "Keyboard", "aggregate://keyboards", true));
            vm.Devices.Add(Row("All Mice (Merged)", "Mouse", "aggregate://mice", true));
            vm.Devices.Add(Row("Headset Microphone", "Microphone", "mic://capture/0", true));
            vm.RefreshCounts();
            return vm;
        }

        // ── 1, 2: the lists a row stands in ──

        [Theory]
        [InlineData(@"\\?\HID#VID_054C&PID_0CE6&MI_03#8&1&0&0000", "Gamepad", true, DeviceListGroup.Connected)]
        [InlineData(@"\\?\HID#VID_054C&PID_0CE6&MI_03#8&1&0&0000", "Gamepad", false, DeviceListGroup.NotConnected)]
        [InlineData("aggregate://keyboards", "Keyboard", true, DeviceListGroup.Other)]
        [InlineData("aggregate://keyboards", "Keyboard", false, DeviceListGroup.Other)]
        [InlineData("web://controller/1", "Gamepad", true, DeviceListGroup.Other)]
        [InlineData("web://controller/1", "Gamepad", false, DeviceListGroup.Other)]
        [InlineData("peer://pc/1", "Gamepad", true, DeviceListGroup.Other)]
        [InlineData("peer://pc/1", "Gamepad", false, DeviceListGroup.Other)]
        [InlineData("mic://x", "Microphone", true, DeviceListGroup.Other)]
        [InlineData("nfc://x", "Nfc", true, DeviceListGroup.Other)]
        [InlineData("headtrack://x", "HeadTracker", true, DeviceListGroup.Other)]
        [InlineData("twitch://x", "TwitchChat", false, DeviceListGroup.Other)]
        [InlineData("midi://x", "Midi", true, DeviceListGroup.Connected)]
        [InlineData("midi://x", "Midi", false, DeviceListGroup.NotConnected)]
        [InlineData("falcon://x", "NovintFalcon", true, DeviceListGroup.Connected)]
        [InlineData("falcon://x", "NovintFalcon", false, DeviceListGroup.NotConnected)]
        [InlineData("analogkb://x", "AnalogKeyboard", true, DeviceListGroup.Connected)]
        [InlineData("analogkb://x", "AnalogKeyboard", false, DeviceListGroup.NotConnected)]
        [InlineData(@"\\?\HID#VID_046D&PID_C33F&MI_01&Col02#8&1&0&0001", "ConsumerControl", true, DeviceListGroup.Other)]
        public void ARowStandsInTheListItsStateAndPathSay(string path, string typeKey, bool online, DeviceListGroup expected)
        {
            var row = new DeviceRowViewModel { DevicePath = path, DeviceTypeKey = typeKey, IsOnline = online };
            Assert.Equal(expected, row.ListGroup);
            Assert.Equal(expected, DeviceRowViewModel.GroupOf(online, row.IsOtherInput));
        }

        [Fact]
        public void AListGroupIsRaisedOnlyWhenItChanges()
        {
            var row = new DeviceRowViewModel { DevicePath = DualSenseUsb, DeviceTypeKey = "Gamepad" };
            Assert.Equal(DeviceListGroup.NotConnected, row.ListGroup);
            int raised = 0;
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DeviceRowViewModel.ListGroup)) raised++; };
            row.IsOnline = true;
            row.IsOnline = true;
            Assert.Equal(1, raised);
            Assert.Equal(DeviceListGroup.Connected, row.ListGroup);
        }

        // ── 3, 4, 5: the segments, the search and the counts ──

        /// <summary>The segment of every type key PopulateDeviceRow writes.</summary>
        private static readonly Dictionary<string, DeviceFilter> Segments = new()
        {
            ["Gamepad"] = DeviceFilter.Controllers, ["Joystick"] = DeviceFilter.Controllers,
            ["Wheel"] = DeviceFilter.Controllers, ["FlightStick"] = DeviceFilter.Controllers,
            ["FirstPerson"] = DeviceFilter.Controllers, ["Supplemental"] = DeviceFilter.Controllers,
            ["NovintFalcon"] = DeviceFilter.Controllers, ["VrController"] = DeviceFilter.Controllers,
            ["Keyboard"] = DeviceFilter.KeyboardsMice, ["AnalogKeyboard"] = DeviceFilter.KeyboardsMice,
            ["Mouse"] = DeviceFilter.KeyboardsMice, ["Touchpad"] = DeviceFilter.KeyboardsMice,
            ["Tablet"] = DeviceFilter.Other, ["Midi"] = DeviceFilter.Other, ["Nfc"] = DeviceFilter.Other,
            ["Microphone"] = DeviceFilter.Other, ["ConsumerControl"] = DeviceFilter.Other,
            ["HeadsetMotion"] = DeviceFilter.Other, ["HandheldButtons"] = DeviceFilter.Other,
            ["SystemMotion"] = DeviceFilter.Other, ["HeadTracker"] = DeviceFilter.Other,
            ["LogitechGKeys"] = DeviceFilter.Other, ["WebMenus"] = DeviceFilter.Other,
            ["PeripheralLighting"] = DeviceFilter.Other, ["PeripheralHaptics"] = DeviceFilter.Other,
            ["KeyboardScreenKeys"] = DeviceFilter.Other, ["GameTelemetry"] = DeviceFilter.Other,
            ["TwitchChat"] = DeviceFilter.Other, ["Device"] = DeviceFilter.Other,
        };

        public static IEnumerable<object[]> SegmentCases => Segments.Select(p => new object[] { p.Key, p.Value });

        [Theory]
        [MemberData(nameof(SegmentCases))]
        public void EachTypeKeyFallsInOneSegment(string typeKey, DeviceFilter expected)
            => Assert.Equal(expected, DevicesViewModel.FilterOf(typeKey));

        /// <summary>The cases above are every key PopulateDeviceRow writes,
        /// so a key added there needs its segment here.</summary>
        [Fact]
        public void EveryTypeKeyThePopulatorWrites_HasACase()
        {
            string input = RepoText("PadForge.App", "Services", "InputService.cs");
            int at = input.IndexOf("row.DeviceTypeKey = ud.CapType switch", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = input.Substring(at, input.IndexOf("};", at, StringComparison.Ordinal) - at);
            var keys = System.Text.RegularExpressions.Regex.Matches(body, "=> \"(\\w+)\"").Select(m => m.Groups[1].Value).ToList();
            Assert.True(keys.Count > 20, keys.Count + " keys");
            Assert.Equal(Segments.Keys.OrderBy(k => k, StringComparer.Ordinal), keys.OrderBy(k => k, StringComparer.Ordinal));
        }

        [Fact]
        public void TheSearchReadsNameProductTypeAndVidPid()
        {
            var vm = new DevicesViewModel();
            var dualSense = Row("DualSense Wireless Controller", "Gamepad", DualSenseUsb, true, 0x054C, 0x0CE6);
            var wheel = Row("G29 Driving Force", "Wheel", @"\\?\HID#VID_046D&PID_C24F#8&6&0&0000", true, 0x046D, 0xC24F, product: "G29");
            var mouse = Row("G502", "Mouse", @"\\?\HID#VID_046D&PID_C08B&MI_00#8&4&0&0000", true, 0x046D, 0xC08B, product: "Gaming Mouse G502 HERO");
            vm.Devices.Add(dualSense);
            vm.Devices.Add(wheel);
            vm.Devices.Add(mouse);

            Assert.True(vm.Matches(dualSense) && vm.Matches(wheel) && vm.Matches(mouse), "control: no search, every row");
            vm.SearchText = "dual";
            Assert.True(vm.Matches(dualSense));
            Assert.False(vm.Matches(wheel) || vm.Matches(mouse));
            // The type in the language, case ignored: the wheel's name and
            // product do not hold it.
            Assert.DoesNotContain(wheel.DeviceType, wheel.DeviceName, StringComparison.OrdinalIgnoreCase);
            vm.SearchText = wheel.DeviceType.ToUpperInvariant();
            Assert.True(vm.Matches(wheel));
            Assert.False(vm.Matches(dualSense));
            vm.SearchText = "054c:0ce6";
            Assert.True(vm.Matches(dualSense));
            Assert.False(vm.Matches(wheel) || vm.Matches(mouse));
            vm.SearchText = "hero";
            Assert.True(vm.Matches(mouse), "the product's name");
            // A segment and a search together.
            vm.SearchText = "dual";
            vm.Filter = DeviceFilter.KeyboardsMice;
            Assert.False(vm.Matches(dualSense));
            Assert.True(vm.ShowNoMatch);
            vm.Filter = DeviceFilter.Controllers;
            Assert.True(vm.Matches(dualSense));
            Assert.False(vm.ShowNoMatch);
        }

        [Fact]
        public void TheCountsAndHintsFollowTheFilter()
        {
            var vm = BoardSet();
            Assert.Equal(5, vm.ConnectedTotal);
            Assert.Equal(5, vm.ConnectedShown);
            Assert.Equal(2, vm.NotConnectedCount);
            Assert.Equal("Logitech G29 Driving Force Racing Wheel, DualShock 3", vm.NotConnectedHint);
            Assert.Equal(3, vm.OtherInputsCount);
            Assert.Equal("All Keyboards (Merged), All Mice (Merged), Headset Microphone", vm.OtherInputsHint);
            Assert.True(vm.HasNotConnected && vm.HasOtherInputs);
            Assert.False(vm.ShowNoMatch);

            vm.Filter = DeviceFilter.KeyboardsMice;
            Assert.Equal(2, vm.ConnectedShown);
            Assert.Equal(5, vm.ConnectedTotal);
            Assert.Equal(0, vm.NotConnectedCount);
            Assert.False(vm.HasNotConnected);
            Assert.Equal(2, vm.OtherInputsCount);

            vm.Filter = DeviceFilter.All;
            vm.SearchText = "no device is called this";
            Assert.True(vm.ShowNoMatch);
            Assert.Equal(0, vm.ConnectedShown);
            Assert.Equal(5, vm.ConnectedTotal);
        }

        // ── 6: the list views ──

        [Fact]
        public void TheListViewsSplitTheRowsByGroup() => RunSta(() =>
        {
            var vm = BoardSet();
            vm.EnsureListViews();
            DeviceRowViewModel[] Rows(System.ComponentModel.ICollectionView view) => view.Cast<DeviceRowViewModel>().ToArray();
            // The connected rows stand in their parts, each part's rows in
            // the collection's order (DevicesBoardTests).
            Assert.Equal(vm.RowsOf(DeviceListGroup.Connected, inParts: true), Rows(vm.ConnectedView));
            Assert.Equal(vm.Devices.Where(d => d.ListGroup == DeviceListGroup.Connected).OrderBy(d => (int)d.Section), Rows(vm.ConnectedView));
            Assert.Equal(vm.Devices.Where(d => d.ListGroup == DeviceListGroup.NotConnected), Rows(vm.NotConnectedView));
            Assert.Equal(vm.Devices.Where(d => d.ListGroup == DeviceListGroup.Other), Rows(vm.OtherInputsView));
            Assert.Equal(5, Rows(vm.ConnectedView).Length);

            // A row going offline leaves the Connected list for Not Connected
            // at the refresh that every place that changes a row calls.
            var pro = vm.Devices.Single(d => d.DeviceName == "Nintendo Switch Pro Controller");
            pro.IsOnline = false;
            vm.RefreshCounts();
            Assert.DoesNotContain(pro, Rows(vm.ConnectedView));
            Assert.Contains(pro, Rows(vm.NotConnectedView));

            // The segment filters every view at once.
            vm.Filter = DeviceFilter.KeyboardsMice;
            Assert.All(Rows(vm.ConnectedView).Concat(Rows(vm.OtherInputsView)),
                d => Assert.Equal(DeviceFilter.KeyboardsMice, DevicesViewModel.FilterOf(d.DeviceTypeKey)));
            Assert.Empty(Rows(vm.NotConnectedView));
        });

        /// <summary>A device that comes online as the list is sorted shows
        /// once, in the collection's order. InputService's sync sets a row
        /// online and then moves rows to sort them, in one turn of the
        /// dispatcher. A view that filtered live took the move as the row's
        /// arrival and took the row again when it caught up with the change,
        /// so every connected device showed twice (2026-11-12, on the
        /// running app: seven connected, fourteen rows), and a row that came
        /// online later stood at the list's end whatever its name.</summary>
        [Fact]
        public void ARowThatComesOnlineAsTheListIsSorted_ShowsOnceAndInOrder() => RunSta(() =>
        {
            var vm = new DevicesViewModel();
            // As the sync first sees them: remembered, not connected yet, in
            // no order.
            var mouse = Row("Mouse", "Mouse", @"\\?\HID#VID_046D&PID_C08B&MI_00#8&4&0&0000", false, 0x046D, 0xC08B);
            var keyboard = Row("Keyboard", "Keyboard", @"\\?\HID#VID_046D&PID_C33F&MI_00#8&5&0&0000", false, 0x046D, 0xC33F);
            var gamepad = Row("Gamepad", "Gamepad", DualSenseBluetooth, false, 0x054C, 0x0CE6);
            vm.Devices.Add(mouse);
            vm.Devices.Add(keyboard);
            vm.Devices.Add(gamepad);
            vm.RefreshCounts();
            vm.EnsureListViews();
            DeviceRowViewModel[] Rows(System.ComponentModel.ICollectionView view) => view.Cast<DeviceRowViewModel>().ToArray();
            void Settle() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Settle();
            Assert.Empty(Rows(vm.ConnectedView));
            Assert.Equal(new[] { mouse, keyboard, gamepad }, Rows(vm.NotConnectedView));

            // The sync's next pass: each comes online, and then the rows are
            // sorted by name with moves, as SyncDevicesList sorts them.
            void SortAsTheSyncDoes()
            {
                var sorted = vm.Devices.OrderBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase).ToList();
                for (int i = 0; i < sorted.Count; i++)
                {
                    int current = vm.Devices.IndexOf(sorted[i]);
                    if (current != i) vm.Devices.Move(current, i);
                }
                vm.RefreshCounts();
            }
            foreach (var row in vm.Devices) row.IsOnline = true;
            SortAsTheSyncDoes();
            Settle();
            Assert.Equal(new[] { gamepad, keyboard, mouse }, Rows(vm.ConnectedView));
            Assert.Equal(3, vm.ConnectedShown);
            Assert.Empty(Rows(vm.NotConnectedView));

            // One goes away and comes back: it stands where its name puts it.
            gamepad.IsOnline = false;
            SortAsTheSyncDoes();
            Settle();
            Assert.Equal(new[] { keyboard, mouse }, Rows(vm.ConnectedView));
            Assert.Equal(new[] { gamepad }, Rows(vm.NotConnectedView));
            gamepad.IsOnline = true;
            SortAsTheSyncDoes();
            Settle();
            Assert.Equal(new[] { gamepad, keyboard, mouse }, Rows(vm.ConnectedView));
        });

        // ── 7, 8: a row's line and its Use It ──

        [Fact]
        public void TheLineNamesBluetoothOnly_AndTheChargeAndCharging()
        {
            var s = Strings.Instance;
            var bt = Row("DualSense Wireless Controller", "Gamepad", DualSenseBluetooth, true, 0x054C, 0x0CE6);
            Assert.True(bt.IsBluetoothLink, "control: the Bluetooth path is a Bluetooth link");
            bt.BatteryPercent = 82;
            string battery = s.Devices_Line_Battery_Format;
            int at = battery.IndexOf("{0}", StringComparison.Ordinal);
            Assert.True(at >= 0);
            Assert.Equal(s.DeviceType_Gamepad + ", " + s.Pad_Transport_Bluetooth + ", " + battery[..at], bt.LineLead);
            Assert.Equal("82%", bt.LineBattery);
            Assert.Equal(battery[(at + 3)..], bt.LineTail);
            Assert.Equal(s.DeviceType_Gamepad + ", " + s.Pad_Transport_Bluetooth + ", " + string.Format(battery, "82%"), bt.RowLine);

            bt.BatteryCharging = true;
            Assert.Equal(s.DeviceType_Gamepad + ", " + s.Pad_Transport_Bluetooth + ", "
                + string.Format(s.Devices_Line_BatteryCharging_Format, "82%"), bt.RowLine);

            var usb = Row("DualSense Wireless Controller", "Gamepad", DualSenseUsb, true, 0x054C, 0x0CE6);
            Assert.False(usb.IsBluetoothLink);
            Assert.Equal(s.DeviceType_Gamepad, usb.RowLine);
            Assert.Equal(string.Empty, usb.LineBattery);
            Assert.Equal(string.Empty, usb.LineTail);
        }

        /// <summary>A battery shows as a picture of its level before the
        /// percentage. The glyph is the Segoe MDL2 battery of the nearest
        /// tenth, from the charging set while the device charges, and a
        /// device that reports no battery has none.</summary>
        [Fact]
        public void TheBatteryPicture_FollowsTheLevelAndTheCharging()
        {
            Assert.Equal(string.Empty, PadForge.Common.BatteryGlyphs.For(-1, false));
            Assert.Equal("", PadForge.Common.BatteryGlyphs.For(0, false));
            Assert.Equal("", PadForge.Common.BatteryGlyphs.For(4, false));
            Assert.Equal("", PadForge.Common.BatteryGlyphs.For(5, false));
            Assert.Equal("", PadForge.Common.BatteryGlyphs.For(66, false));
            Assert.Equal("", PadForge.Common.BatteryGlyphs.For(94, false));
            Assert.Equal("", PadForge.Common.BatteryGlyphs.For(95, false));
            Assert.Equal("", PadForge.Common.BatteryGlyphs.For(100, false));
            Assert.Equal("", PadForge.Common.BatteryGlyphs.For(0, true));
            Assert.Equal("", PadForge.Common.BatteryGlyphs.For(66, true));
            Assert.Equal("", PadForge.Common.BatteryGlyphs.For(84, true));
            Assert.Equal("", PadForge.Common.BatteryGlyphs.For(90, true));
            Assert.Equal("", PadForge.Common.BatteryGlyphs.For(100, true));

            var bt = Row("DualSense Wireless Controller", "Gamepad", DualSenseBluetooth, true, 0x054C, 0x0CE6);
            var raised = new List<string>();
            bt.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            Assert.Equal(string.Empty, bt.BatteryGlyph);
            Assert.Equal(string.Empty, bt.BatteryGap);
            Assert.Equal(string.Empty, bt.BatteryLine);
            bt.BatteryPercent = 66;
            Assert.Equal("", bt.BatteryGlyph);
            Assert.Equal(" ", bt.BatteryGap);
            Assert.Contains(nameof(DeviceRowViewModel.BatteryGlyph), raised);
            Assert.Contains(nameof(DeviceRowViewModel.BatteryGap), raised);
            Assert.Contains(nameof(DeviceRowViewModel.LineFacts), raised);
            raised.Clear();
            bt.BatteryCharging = true;
            Assert.Equal("", bt.BatteryGlyph);
            Assert.Contains(nameof(DeviceRowViewModel.BatteryGlyph), raised);
            Assert.Contains(nameof(DeviceRowViewModel.BatteryLine), raised);
            Assert.Equal("66%" + bt.LineTail, bt.BatteryLine);
        }

        /// <summary>The line as it is drawn stops before the battery's word:
        /// the type and the link, and one more comma when a battery
        /// follows. The words stay whole in RowLine, which a screen reader
        /// hears.</summary>
        [Fact]
        public void TheDrawnLineLeavesTheBatterysWordToItsPicture()
        {
            var s = Strings.Instance;
            var bt = Row("DualSense Wireless Controller", "Gamepad", DualSenseBluetooth, true, 0x054C, 0x0CE6);
            Assert.Equal(s.DeviceType_Gamepad + ", " + s.Pad_Transport_Bluetooth, bt.LineFacts);
            bt.BatteryPercent = 82;
            Assert.Equal(s.DeviceType_Gamepad + ", " + s.Pad_Transport_Bluetooth + ", ", bt.LineFacts);
            Assert.StartsWith(bt.LineFacts, bt.LineLead);
            Assert.NotEqual(bt.LineFacts, bt.LineLead);
            Assert.Equal(bt.LineLead + bt.LineBattery + bt.LineTail, bt.RowLine);
            var usb = Row("DualSense Wireless Controller", "Gamepad", DualSenseUsb, true, 0x054C, 0x0CE6);
            Assert.Equal(s.DeviceType_Gamepad, usb.LineFacts);
            usb.BatteryPercent = 40;
            Assert.Equal(s.DeviceType_Gamepad + ", ", usb.LineFacts);
        }

        /// <summary>Wherever a view shows a battery's percentage, a picture
        /// of its level stands before it: the meter, or a glyph in the font
        /// the glyphs are in. The rebuild had written the battery as a bare
        /// number on this page's rows and panel, the controller page's
        /// pills, Add a Controller and Home's first card, while Home's
        /// cards kept the picture. A text made of such runs carries its
        /// words as its name, so a screen reader never meets the glyph.</summary>
        [Fact]
        public void EveryBatteryPercentageFollowsItsPicture()
        {
            var percent = new System.Text.RegularExpressions.Regex(@"^\{Binding (?:SelectedDevice\.)?(?:BatteryText|LineBattery|FirstRunLineBattery)\b");
            var picture = new System.Text.RegularExpressions.Regex(@"^\{Binding (?:SelectedDevice\.)?(?:BatteryGlyph|FirstRunLineGlyph)\b");
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "PadForge.sln"))) dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            string app = Path.Combine(dir, "PadForge.App");
            var places = new List<string>();
            int meters = 0;
            foreach (string path in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(app, path).Replace('\\', '/');
                if (rel.StartsWith("obj/", StringComparison.Ordinal) || rel.StartsWith("bin/", StringComparison.Ordinal)) continue;
                XDocument doc = XDocument.Load(path);
                foreach (XElement shown in doc.Descendants().Where(e => percent.IsMatch((string)e.Attribute("Text") ?? string.Empty)))
                {
                    places.Add(rel);
                    // An element holds the glyph alone and is hidden with the
                    // battery. A run among a text's runs names a text font
                    // first (TheBatteryPicturesRun_DrawsNothingWhileItIsEmpty).
                    string font = shown.Name.LocalName == "Run" ? PadForge.Common.BatteryGlyphs.RunFontFamily : PadForge.Common.BatteryGlyphs.FontFamily;
                    bool meter = shown.ElementsBeforeSelf().Any(e => e.Name.LocalName == "BatteryMeter"
                        && ((string)e.Attribute("Percent") ?? string.Empty).StartsWith("{Binding ", StringComparison.Ordinal));
                    if (meter) meters++;
                    Assert.True(meter || shown.ElementsBeforeSelf().Any(e => picture.IsMatch((string)e.Attribute("Text") ?? string.Empty)
                            && ((string)e.Attribute("FontFamily") ?? string.Empty) == font),
                        rel + ": a battery percentage with no picture of the level before it, a meter or a glyph in " + font);
                    if (shown.Name.LocalName != "Run") continue;
                    Assert.False(string.IsNullOrEmpty((string)shown.Parent.Attribute("AutomationProperties.Name")),
                        rel + ": a text with a battery picture among its runs has no name of its own");
                }
            }
            // Positive control: Home's row of a device, this page's row,
            // panel and Details card, the controller page's pill, Add a
            // Controller's choice and Home's first card. Home's row is a
            // meter.
            Assert.True(places.Count >= 7, places.Count + " battery percentages were found: " + string.Join(", ", places));
            Assert.True(meters >= 1, "no battery percentage follows a meter");
        }

        /// <summary>A text whose battery picture is one of its runs draws
        /// nothing for that run while the device has no battery. A run with
        /// no text still takes its font for what WPF sets at its edges, and
        /// the icon font draws those as two boxes: the first build of the
        /// picture showed "Mouse" and two boxes on every row with no
        /// battery. The run's font list, a text font before the icon font,
        /// draws the glyph and draws nothing when it is empty.</summary>
        [Fact]
        public void TheBatteryPicturesRun_DrawsNothingWhileItIsEmpty()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    double Width(string font, string glyph)
                    {
                        var text = new System.Windows.Controls.TextBlock { FontSize = 26 };
                        text.Inlines.Add(new System.Windows.Documents.Run("Mouse"));
                        if (font != null)
                            text.Inlines.Add(new System.Windows.Documents.Run(glyph) { FontFamily = new System.Windows.Media.FontFamily(font) });
                        text.Measure(new System.Windows.Size(900, 100));
                        return text.DesiredSize.Width;
                    }
                    double plain = Width(null, null);
                    Assert.True(plain > 0);
                    Assert.Equal(plain, Width(PadForge.Common.BatteryGlyphs.RunFontFamily, string.Empty));
                    // The control: the icon font alone does draw for an empty run.
                    Assert.True(Width(PadForge.Common.BatteryGlyphs.FontFamily, string.Empty) > plain + 1,
                        "an empty run in the icon font no longer draws: the run's font list may not be needed");
                    // And the list still reaches the glyph: as wide as in the icon font alone.
                    string glyph = PadForge.Common.BatteryGlyphs.For(66, false);
                    Assert.Equal(Width(PadForge.Common.BatteryGlyphs.FontFamily, glyph), Width(PadForge.Common.BatteryGlyphs.RunFontFamily, glyph));
                    Assert.True(Width(PadForge.Common.BatteryGlyphs.RunFontFamily, glyph) > plain + 1);
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            Assert.True(thread.Join(30000), "the text measure timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();

            // Every run that holds a glyph among a text's runs has that list.
            foreach (string view in new[] { "DevicesPage.xaml", "AddControllerView.xaml", "HomePage.xaml" })
            {
                XDocument doc = XDocument.Parse(RepoText("PadForge.App", "Views", view));
                foreach (XElement run in doc.Descendants().Where(e => e.Name.LocalName == "Run"
                             && ((string)e.Attribute("FontFamily") ?? string.Empty).Contains("Segoe MDL2 Assets", StringComparison.Ordinal)))
                    Assert.Equal(PadForge.Common.BatteryGlyphs.RunFontFamily, (string)run.Attribute("FontFamily"));
            }
        }

        /// <summary>The Details card, as the card it took the place of: the
        /// Link row draws the Bluetooth mark before the word, and a device
        /// that reports a battery has a Battery row with the picture, the
        /// percentage and charging.</summary>
        [Fact]
        public void TheDetailsCard_DrawsTheLinksMarkAndHasABatteryRow()
        {
            var page = Page();
            string S(string key) => "{Binding " + key + ", Source={x:Static strings:Strings.Instance}}";
            // The table's head and the panel's tiles name the two as well.
            var details = Named(page, "DetailsFold").Descendants().ToList();
            XElement link = Assert.Single(details, e => (string)e.Attribute("Text") == S("Devices_Detail_Link"));
            XElement linkValue = link.ElementsAfterSelf().First();
            Assert.Equal(S("Pad_Transport_Bluetooth"), (string)linkValue.Attribute("AutomationProperties.Name"));
            var linkRuns = linkValue.Elements().ToList();
            Assert.Equal("", (string)linkRuns[0].Attribute("Text"));
            Assert.Equal(PadForge.Common.BatteryGlyphs.RunFontFamily, (string)linkRuns[0].Attribute("FontFamily"));
            Assert.StartsWith("{Binding Pad_Transport_Bluetooth,", (string)linkRuns[^1].Attribute("Text"));

            XElement battery = Assert.Single(details, e => (string)e.Attribute("Text") == S("Devices_Detail_Battery"));
            Assert.Equal("{Binding SelectedDevice.HasBattery, Converter={StaticResource BoolToVisibilityConverter}}", (string)battery.Parent.Attribute("Visibility"));
            Assert.Same(link.Parent, battery.Parent.ElementsBeforeSelf().Last());
            XElement value = battery.ElementsAfterSelf().First();
            Assert.StartsWith("{Binding SelectedDevice.BatteryLine,", (string)value.Attribute("AutomationProperties.Name"));
            Assert.Equal(new[] { "SelectedDevice.BatteryGlyph", "SelectedDevice.BatteryGap", "SelectedDevice.BatteryText", "SelectedDevice.LineTail" },
                value.Elements().Select(r => ((string)r.Attribute("Text")).Split(',')[0].Replace("{Binding ", string.Empty)).ToArray());
            Assert.Equal("Battery", XDocument.Parse(RepoText("PadForge.App", "Resources", "Strings", "Strings.resx"))
                .Root.Elements("data").Single(d => (string)d.Attribute("name") == "Devices_Detail_Battery").Element("value").Value);
        }

        [Fact]
        public void UseIt_IsOfferedForAnOnlineControllerNobodyUses()
        {
            var pad = Row("Pad", "Gamepad", DualSenseUsb, true);
            Assert.True(pad.OffersUse);
            Assert.False(Row("Mouse", "Mouse", @"\\?\HID#VID_046D&PID_C08B", true).OffersUse);
            Assert.False(Row("Away", "Gamepad", DualSenseUsb, false).OffersUse);
            var used = Row("Used", "Gamepad", DualSenseUsb, true);
            used.SlotBadges.Add(new SlotBadge { VcType = VirtualControllerType.Xbox, TypeNumber = 1 });
            Assert.True(used.IsAssigned);
            Assert.False(used.OffersUse);
        }

        // ── 9, 10: the pills, the chips and New Controller ──

        private static void WithSlots(Action body)
        {
            bool[] created = SettingsManager.SlotCreated;
            var orders = VirtualControllerGroups.InOrder.ToDictionary(t => t, t => SettingsManager.SlotOrders.GetOrderFor(t).ToList());
            try
            {
                SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
                foreach (var t in VirtualControllerGroups.InOrder) SettingsManager.SlotOrders.GetOrderFor(t).Clear();
                body();
            }
            finally
            {
                SettingsManager.SlotCreated = created;
                foreach (var pair in orders)
                {
                    var list = SettingsManager.SlotOrders.GetOrderFor(pair.Key);
                    list.Clear();
                    list.AddRange(pair.Value);
                }
            }
        }

        private static void Create(int padIndex, VirtualControllerType type)
        {
            SettingsManager.SlotCreated[padIndex] = true;
            SettingsManager.SlotOrders.Add(padIndex, type);
        }

        [Fact]
        public void ThePillsAndChipsNumberWithinTheirType() => WithSlots(() =>
        {
            Create(0, VirtualControllerType.Xbox);
            Create(1, VirtualControllerType.KeyboardMouse);
            Create(2, VirtualControllerType.Xbox);
            var vm = new DevicesViewModel();
            vm.RefreshSlotButtons();
            var s = Strings.Instance;
            Assert.Equal(new[] { s.ControllerType_Xbox + " #1", s.ControllerType_Xbox + " #2", s.ControllerType_KeyboardMouse + " #1" },
                vm.ActiveSlotItems.Select(i => i.Label));
            Assert.Equal(new[] { 1, 2, 3 }, vm.ActiveSlotItems.Select(i => i.SlotNumber));

            var row = Row("Pad", "Gamepad", DualSenseUsb, true);
            row.SetAssignedSlots(new List<int> { 2 });
            var pill = Assert.Single(row.SlotBadges);
            Assert.Equal(s.ControllerType_Xbox + " #2", pill.Label);
            Assert.Equal(s.ControllerType_Xbox + " #2", row.RowStatus);
            row.SetAssignedSlots(new List<int>());
            Assert.Equal(s.Devices_NotInUse, row.RowStatus);
        });

        [Fact]
        public void NewController_HidesAtTheSlotCap() => WithSlots(() =>
        {
            for (int i = 0; i < InputManager.MaxPads - 1; i++)
                Create(i, VirtualControllerType.Xbox);
            var vm = new DevicesViewModel();
            vm.RefreshSlotButtons();
            Assert.True(vm.CanAddController);
            Create(InputManager.MaxPads - 1, VirtualControllerType.Xbox);
            vm.RefreshSlotButtons();
            Assert.False(vm.CanAddController);
        });

        // ── 11, 12, 13: the Settings card ──

        [Fact]
        public void ResetAll_ResetsWhatTheCardShows_AndNotifiesOnce()
        {
            var vm = new DevicesViewModel();
            var row = Row("DualSense Wireless Controller", "Gamepad", DualSenseBluetooth, true, 0x054C, 0x0CE6);
            row.IsHidHideAvailable = true;
            row.ShowIdleDisconnect = true;
            row.ShowQuickCharge = true;
            row.HidHideEnabled = true;
            row.IdleDisconnectMinutes = 8;
            row.QuickChargeEnabled = true;
            row.ForceRawJoystickMode = true;
            vm.SelectedDevice = row;
            vm.LastRawStateDeviceGuid = row.InstanceGuid;
            var notified = new List<Guid>();
            vm.DeviceHidingChanged += (_, guid) => notified.Add(guid);
            Assert.True(vm.HasResettableSettings);
            Assert.True(vm.ResetSettingsCardCommand.CanExecute(null));

            vm.ResetSettingsCardCommand.Execute(null);
            Assert.False(row.HidHideEnabled);
            Assert.Equal(0, row.IdleDisconnectMinutes);
            Assert.False(row.QuickChargeEnabled);
            Assert.True(row.ForceRawJoystickMode, "Force Raw Joystick Mode is the Details card's");
            Assert.Equal(new[] { row.InstanceGuid }, notified);
            Assert.Equal(Guid.Empty, vm.LastRawStateDeviceGuid);

            // A Bliss-Box port's arrows persist through the port's request.
            var port = Row("Bliss-Box", "Gamepad", @"\\?\HID#VID_16D0&PID_0D04#8&1&0&0000", true);
            port.ShowNativeArrows = true;
            port.BlissBoxNativeArrows = true;
            vm.SelectedDevice = port;
            var actions = new List<BlissBoxAction>();
            vm.BlissBoxActionRequested += (_, a) => actions.Add(a);
            vm.ResetSettingsCardCommand.Execute(null);
            Assert.False(port.BlissBoxNativeArrows);
            Assert.Equal(new[] { BlissBoxAction.NativeArrows }, actions);
            Assert.Equal(new[] { row.InstanceGuid, port.InstanceGuid }, notified);
        }

        [Fact]
        public void EveryResetInTheSettingsCard_IsOneResetAllCovers()
        {
            var row = Row("Keyboard", "Keyboard", @"\\?\HID#VID_046D&PID_C33F&MI_00#8&5&0&0000", true, 0x046D, 0xC33F);
            row.IsHidHideAvailable = true;
            row.ShowIdleDisconnect = true;
            row.ShowQuickCharge = true;
            row.ShowNativeArrows = true;
            row.ShowGunCalibration = true;
            var covered = DevicesViewModel.SettingsCardResets(row);
            Assert.Equal(6, covered.Count);

            var page = Page();
            var inCard = Named(page, "SettingsCard").Descendants().Where(e => e.Name.LocalName == "SettingResetButton")
                .Select(e => Attr(e, "CommandParameter")).ToList();
            Assert.Equal(covered.OrderBy(n => n, StringComparer.Ordinal), inCard.OrderBy(n => n, StringComparer.Ordinal));
            Assert.Contains(Named(page, "DetailsFold").Descendants(),
                e => e.Name.LocalName == "SettingResetButton" && Attr(e, "CommandParameter") == nameof(DeviceRowViewModel.ForceRawJoystickMode));
        }

        [Fact]
        public void TheSettingsCardShowsWhenAnythingInItShows()
        {
            var vm = new DevicesViewModel();
            vm.SelectedDevice = Row("Web Pad", "Gamepad", "web://controller/1", true);
            Assert.False(vm.ShowSettingsCard);
            Assert.False(vm.HasResettableSettings);

            vm.SelectedDevice = Row("Keyboard", "Keyboard", @"\\?\HID#VID_046D&PID_C33F&MI_00#8&5&0&0000", true, 0x046D, 0xC33F);
            Assert.True(vm.ShowSettingsCard);

            vm.SelectedDevice = Row("DualSense Wireless Controller", "Gamepad", DualSenseUsb, true, 0x054C, 0x0CE6);
            Assert.True(vm.ShowSettingsCard);

            vm.SelectedDevice = Row("Web Pad", "Gamepad", "web://controller/1", true);
            Assert.False(vm.ShowSettingsCard);
            vm.ApplyDriverOffer(new PadForge.Services.VendorUsbDriverInstaller.OptInOffer(
                PadForge.Services.VendorUsbDriverInstaller.OptInKind.TouchSenseMouse, null, Array.Empty<string>(), "node", true));
            Assert.True(vm.ShowDeviceTools);
            Assert.True(vm.ShowSettingsCard);
        }

        // ── 14: the compact test ──

        /// <summary>A stick's ring in the compact test is the stick's full
        /// travel: a full push along one axis puts the dot against the ring,
        /// and a corner of a square gate beyond it, as the controller page's
        /// stick picture shows a raw position. The dot's square was 40 wide
        /// inside a ring 52 wide, so a full push north, south, east or west
        /// stopped 6 short of the ring and no position ever left the
        /// circle (owner, 2026-11-12). The ring is the board's 80 since the
        /// world-class pass, with a line from its middle to the dot.</summary>
        [Fact]
        public void AFullPushPutsTheTestDotAgainstItsRing()
        {
            var page = Page();
            var rings = Named(page, "GamepadTest").Descendants().Where(e => e.Name.LocalName == "Grid" && Attr(e, "Width") == "84").ToList();
            Assert.Equal(2, rings.Count);
            var converter = new PadForge.Converters.NormToCanvasConverter();
            double At(double value, string parameter)
                => (double)converter.Convert(value, typeof(double), parameter, System.Globalization.CultureInfo.InvariantCulture);
            foreach (var (ring, stick) in rings.Zip(new[] { "TestLeft", "TestRight" }))
            {
                var circle = ring.Elements().Single(e => e.Name.LocalName == "Ellipse");
                var canvas = ring.Elements().Single(e => e.Name.LocalName == "Canvas");
                var dot = canvas.Elements().Single(e => e.Name.LocalName == "Ellipse");
                double width = double.Parse(Attr(circle, "Width"), System.Globalization.CultureInfo.InvariantCulture);
                double size = double.Parse(Attr(dot, "Width"), System.Globalization.CultureInfo.InvariantCulture);
                Assert.Equal("80", Attr(circle, "Width"));
                Assert.Equal("12", Attr(dot, "Width"));
                Assert.Equal(Attr(circle, "Width"), Attr(circle, "Height"));
                // The line runs from the ring's middle to where the stick
                // is: a dot of no size, so a full push ends on the ring.
                var line = canvas.Elements().Single(e => e.Name.LocalName == "Line");
                Assert.Equal("40", Attr(line, "X1"));
                Assert.Equal("40", Attr(line, "Y1"));
                Assert.Equal("{Binding " + stick + "X, Converter={StaticResource NormToCanvasConverter}, ConverterParameter='80,0'}", Attr(line, "X2"));
                Assert.Equal("{Binding " + stick + "Y, Converter={StaticResource NormToCanvasConverter}, ConverterParameter='80,0'}", Attr(line, "Y2"));
                Assert.Equal(width / 2, At(0.5, "80,0"), 6);
                Assert.Equal(width, At(1, "80,0"), 6);
                // The dot moves in the ring's own square.
                Assert.Equal(Attr(circle, "Width"), Attr(canvas, "Width"));
                Assert.Equal(Attr(circle, "Width"), Attr(canvas, "Height"));
                string parameter = Attr(circle, "Width") + "," + Attr(dot, "Width");
                Assert.Equal("{Binding " + stick + "X, Converter={StaticResource NormToCanvasConverter}, ConverterParameter='" + parameter + "'}", Attr(dot, "Canvas.Left"));
                Assert.Equal("{Binding " + stick + "Y, Converter={StaticResource NormToCanvasConverter}, ConverterParameter='" + parameter + "'}", Attr(dot, "Canvas.Top"));

                // At rest the dot is in the middle. A full push puts its
                // edge on the ring's edge, either way.
                Assert.Equal(width / 2, At(0.5, parameter) + size / 2, 6);
                Assert.Equal(width, At(1, parameter) + size, 6);
                Assert.Equal(0, At(0, parameter), 6);
                // A corner of a square gate is past the circle.
                double center = At(1, parameter) + size / 2 - width / 2;
                Assert.True(Math.Sqrt(2) * center > width / 2, "a corner stays inside the ring");

                // The ring is a drawing, so it stands in a picture that
                // reads the stick's name and where the stick is.
                XElement picture = ring.Parent;
                Assert.Equal("NamedPicture", picture.Name.LocalName);
                Assert.Equal("{Binding Stick_" + (stick == "TestLeft" ? "Left" : "Right") + "Thumbstick, Source={x:Static strings:Strings.Instance}}",
                    Attr(picture, "AutomationProperties.Name"));
                XElement where = picture.Elements().Single(e => e.Name.LocalName == "AutomationProperties.HelpText").Elements().Single();
                Assert.Equal("MultiBinding", where.Name.LocalName);
                Assert.Equal("X {0:P0}, Y {1:P0}", Attr(where, "StringFormat"));
                Assert.Equal(new[] { stick + "X", stick + "Y" }, where.Elements().Select(b => Attr(b, "Path")).ToArray());
            }
        }

        [Fact]
        public void TheGamepadTestReadsTheGamepadSlots()
        {
            var vm = new DevicesViewModel();
            vm.RebuildRawStateCollections(new[] { 0, 1, 2, 3, 4, 5 }, Enumerable.Range(0, 15).ToArray(), 1, gamepadLayout: true);
            Assert.True(vm.ShowGamepadTest);
            Assert.Equal(Strings.Instance.Common_None, vm.TestPressedNames);

            var state = new CustomInputState
            {
                Axis = new int[CustomInputState.MaxAxis],
                Buttons = new bool[CustomInputState.MaxButtons],
            };
            state.Axis[0] = 51118;
            state.Axis[1] = 16384;
            state.Axis[3] = 32767;
            state.Axis[4] = 32767;
            state.Axis[5] = 40632;
            state.Buttons[0] = true;
            state.Buttons[4] = true;
            vm.UpdateGamepadTest(state);
            Assert.Equal(0.78, vm.TestLeftX, 2);
            Assert.Equal(0.25, vm.TestLeftY, 2);
            Assert.Equal(0.62, vm.TestRightTrigger, 2);
            Assert.Equal(0, vm.TestLeftTrigger, 2);
            Assert.Equal(PadForge.Common.MappingDisplayResolver.LocalizeObjectName(GamepadObjectNames.Button(0)) + ", "
                + PadForge.Common.MappingDisplayResolver.LocalizeObjectName(GamepadObjectNames.Button(4)), vm.TestPressedNames);

            state.Buttons[0] = state.Buttons[4] = false;
            vm.UpdateGamepadTest(state);
            Assert.Equal(Strings.Instance.Common_None, vm.TestPressedNames);

            // The Pressed line's two parts make the format around the names.
            Assert.Equal(string.Format(Strings.Instance.Devices_Pressed_Format, "A"), vm.TestPressedLead + "A" + vm.TestPressedTail);

            vm.ClearRawState();
            Assert.False(vm.ShowGamepadTest);
            vm.RebuildRawStateCollections(new[] { 0, 1 }, new[] { 0 }, 0);
            Assert.False(vm.ShowGamepadTest, "a device read raw has no compact test");
        }

        // ── 15: the wrap ──

        [Fact]
        public void TheLayoutStacksWhereTheBoardWraps()
        {
            // The list's 520, the 16 between, the panel's 372 and 36 a side.
            Assert.True(DevicesPage.IsStacked(979));
            Assert.False(DevicesPage.IsStacked(980));
            Assert.Equal(320, DevicesPage.SearchWidth(968, 336));
            Assert.Equal(236, DevicesPage.SearchWidth(588, 336));
            Assert.Equal(320, DevicesPage.SearchWidth(500, 336));
            Assert.Equal(180, DevicesPage.SearchWidth(180, 336));
            // The idle row in the panel's 338, with the label's text 100
            // wide and the unit's 128: the row lacks 14, and the unit gives
            // all of it, so the label stays on one line and the unit wraps
            // to two. A wide row leaves the unit its text.
            Assert.Equal(114, DevicesPage.IdleUnitWidth(338, 100, 128));
            Assert.Equal(128, DevicesPage.IdleUnitWidth(554, 100, 128));
            Assert.Equal(128, DevicesPage.IdleUnitWidth(352, 100, 128));
            // A unit as short as its two lines has nothing to give: the
            // two share what the row lacks.
            Assert.Equal(72, DevicesPage.IdleUnitWidth(296, 100, 128));
            Assert.Equal(72 - 20 * 72 / 244.0, DevicesPage.IdleUnitWidth(276, 100, 128), 6);
            // A short unit in a narrow row: 4 over, which it gives whole.
            Assert.Equal(46, DevicesPage.IdleUnitWidth(270, 100, 50));
        }

        // ── 16, 17, 18: the page's parts ──

        [Fact]
        public void ThePageIsTheBoardsParts()
        {
            string text = RepoText("PadForge.App", "Views", "DevicesPage.xaml");
            var page = XDocument.Parse(text);
            var all = page.Descendants().ToList();

            var order = new[] { "HeaderRow", "FilterRow", "DeviceListCard", "IdentityCard", "TestCard", "SettingsCard", "DetailsCard" }
                .Select(n => all.IndexOf(Named(page, n))).ToList();
            Assert.Equal(order.OrderBy(i => i), order);

            var ids = all.Select(e => Attr(e, "AutomationProperties.AutomationId")).Where(v => v != null).ToList();
            foreach (string id in new[]
            {
                "Devices_Pair", "Devices_Refresh", "Devices_Search", "Devices_Filter_All", "Devices_Filter_Controllers",
                "Devices_Filter_KeyboardsMice", "Devices_Filter_Other", "Devices_ListScroll", "Devices_PanelScroll",
                "Devices_List_Connected", "Devices_List_NotConnected", "Devices_List_OtherInputs", "Devices_Fold_NotConnected",
                "Devices_Fold_OtherInputs", "Devices_UseIt", "Devices_TestRumble", "Devices_UsedBy_New", "Devices_ResetAll",
                "Devices_More_TestInputs", "Devices_Details", "Devices_CopyDetails", "Devices_SubmitMapping", "Devices_Remove",
            })
                Assert.True(ids.Count(v => v == id) == 1, id);

            var resets = all.Where(e => e.Name.LocalName == "SettingResetButton")
                .ToDictionary(e => Attr(e, "AutomationProperties.AutomationId"), e => Attr(e, "CommandParameter"));
            Assert.Equal(new Dictionary<string, string>
            {
                ["Reset_DeviceRowViewModel_ForceRawJoystickMode_1004"] = "ForceRawJoystickMode",
                ["Reset_DeviceRowViewModel_BlissBoxNativeArrows_1005"] = "BlissBoxNativeArrows",
                ["Reset_DeviceRowViewModel_HidHideEnabled_1024"] = "HidHideEnabled",
                ["Reset_DeviceRowViewModel_ConsumeInputEnabled_1054"] = "ConsumeInputEnabled",
                ["Reset_DeviceRowViewModel_IdleDisconnectMinutes_1094"] = "IdleDisconnectMinutes",
                ["Reset_DeviceRowViewModel_QuickChargeEnabled_1108"] = "QuickChargeEnabled",
                ["Reset_DeviceRowViewModel_GunCalibration"] = "GunCalibration",
            }, resets);

            var keys = all.Select(e => Attr(e, "StateKey")).Where(v => v != null).OrderBy(v => v, StringComparer.Ordinal);
            Assert.Equal(new[] { "Devices.Details", "Devices.NotConnected", "Devices.OtherInputs", "Devices.TestInputs.More" }, keys);

            foreach (string gone in new[] { "Effect=\"", "Property=\"Effect\"", "DropShadowEffect", "ClickBorder", "UpperConverter", "SectionEyebrow" })
                Assert.DoesNotContain(gone, text);
            // None of the nine strings the old page used and the new one frees.
            foreach (string key in new[] { "Devices_OnlineTotal", "Devices_Total", "Devices_VCAssignment", "Devices_NoSlotsHint",
                "Devices_InputMode", "Devices_InputHiding", "Devices_Power", "Devices_LightGun", "Devices_Product" })
                Assert.DoesNotMatch(@"\b" + key + @"\b", text);

            var handlers = all.SelectMany(e => e.Attributes())
                .Where(a => a.Name.LocalName is "Click" or "SizeChanged" or "LostFocus" or "SelectionChanged" or "Checked"
                    or "PreviewMouseLeftButtonDown" or "PreviewMouseMove" or "PreviewMouseLeftButtonUp" or "Binding.TargetUpdated")
                .Select(a => a.Value).Distinct().ToList();
            Assert.Contains("UseIt_Click", handlers);
            foreach (string handler in handlers)
                Assert.True(typeof(DevicesPage).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) != null,
                    handler + " is no method of DevicesPage");
        }

        [Fact]
        public void TheRowsDragAndOfferUseIt()
        {
            var page = Page();
            var template = page.Descendants().Single(e => e.Name.LocalName == "DataTemplate" && (string)e.Attribute(Xaml + "Key") == "DeviceRowTemplate");
            // The row, beside the template's triggers.
            var root = template.Elements().Single(e => e.Name.LocalName != "DataTemplate.Triggers");
            Assert.Equal("Border", root.Name.LocalName);
            Assert.Equal("DeviceCard_MouseDown", Attr(root, "PreviewMouseLeftButtonDown"));
            Assert.Equal("DeviceCard_MouseMove", Attr(root, "PreviewMouseMove"));
            Assert.Equal("DeviceCard_MouseUp", Attr(root, "PreviewMouseLeftButtonUp"));
            Assert.Equal("UseIt_Click", Attr(Named(page, "UseItButton"), "Click"));
        }

        [Fact]
        public void MainWindowAnswersTheNewControllerRequest()
        {
            string window = RepoText("PadForge.App", "MainWindow.xaml.cs");
            int at = window.IndexOf("DevicesPageView.NewControllerRequested += (s, args) =>", StringComparison.Ordinal);
            Assert.True(at > 0);
            // Add a Controller opens with the device picked to drive the new
            // controller, as Home's offer opens it.
            Assert.Contains("OpenAddController(args.Device, args.Anchor);", window.Substring(at, 200));
        }
    }
}
