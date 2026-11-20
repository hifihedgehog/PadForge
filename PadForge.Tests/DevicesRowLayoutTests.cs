using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PadForge.Common;
using PadForge.Engine;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The Devices list as it is laid out. Every kind of row is one
    /// height with its type under its name, in the table and in the narrow
    /// list, where the link and the battery follow the type on its line.
    /// Before 2026-11-17 a gamepad's and a wheel's type stood under the
    /// name while a keyboard's and a mouse's stood beside it on a shorter
    /// row.</summary>
    [Collection("WpfApplicationSingleton")]
    public class DevicesRowLayoutTests
    {
        // The page beside the sidebar of a 1720-wide window, where the list is
        // a table, and of a 1280-wide one, where the list is under TableMin wide
        // and narrow (DevicesPage.TableMin).
        private const double Table = 1456, NarrowList = 1016, Tall = 2600;

        private const string BluetoothPad =
            @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0ce6#9&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private const string BluetoothKeyboard =
            @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002046d_pid&b35b#9&2&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}";

        private static IEnumerable<T> Under<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T found) yield return found;
                foreach (var deeper in Under<T>(child)) yield return deeper;
            }
        }

        private static Rect In(FrameworkElement part, Visual row)
            => part.TransformToAncestor(row).TransformBounds(new Rect(part.RenderSize));

        private static void Lay(FrameworkElement ground, double width)
        {
            PageRenderDump.Flush(ground);
            ground.Measure(new Size(width, Tall));
            ground.Arrange(new Rect(0, 0, width, Tall));
            ground.UpdateLayout();
        }

        [Fact]
        public void EveryKindOfRowIsOneHeightWithItsTypeUnderItsName()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var vm = new DevicesViewModel();
                DeviceRowViewModel Add(string name, string typeKey, int kind, string path, int battery = -1)
                {
                    var row = new DeviceRowViewModel
                    {
                        InstanceGuid = Guid.NewGuid(), DeviceName = name, ProductName = name, DeviceTypeKey = typeKey,
                        DevicePath = path, IsOnline = true, BatteryPercent = battery,
                        KindGlyph = DeviceTypeGlyph.For(kind), KindShape = DeviceTypeGlyph.ShapeFor(kind),
                    };
                    vm.Devices.Add(row);
                    return row;
                }
                // A row of each kind. One controller and one keyboard have a
                // link and a battery, and one of each has neither, so a
                // row's facts are seen not to change its form.
                var pad = Add("DualSense Wireless Controller", "Gamepad", InputDeviceType.Gamepad, BluetoothPad, 82);
                Add("Xbox Series X Controller", "Gamepad", InputDeviceType.Gamepad, "XInput#0");
                Add("Logitech G29 Driving Force Racing Wheel", "Wheel", InputDeviceType.Driving,
                    @"\\?\HID#VID_046D&PID_C24F#8&6&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}");
                Add("T.16000M", "Joystick", InputDeviceType.Joystick,
                    @"\\?\HID#VID_044F&PID_B10A#8&8&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}");
                var keyboard = Add("MX Keys", "Keyboard", InputDeviceType.Keyboard, BluetoothKeyboard, 40);
                Add("Standard PS/2 Keyboard", "Keyboard", InputDeviceType.Keyboard,
                    @"\\?\ACPI#PNP0303#4&1&0#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}");
                Add("Logitech G502 HERO Gaming Mouse", "Mouse", InputDeviceType.Mouse,
                    @"\\?\HID#VID_046D&PID_C08B&MI_00#8&4&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}");
                Add("HIDI2C Device", "Touchpad", InputDeviceType.Touchpad,
                    @"\\?\HID#GXTP5100&Col01#4&c834cd3&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}");
                Add("Headset Microphone", "Microphone", InputDeviceType.Microphone, "mic://capture/0");
                vm.RefreshCounts();
                // Control: the two rows with facts have them, and a row
                // without has none.
                Assert.True(pad.HasLink && pad.HasBattery, "control: the Bluetooth pad has a link and a battery");
                Assert.True(keyboard.HasLink && keyboard.HasBattery, "control: the Bluetooth keyboard has a link and a battery");
                Assert.Equal(2, vm.Devices.Count(d => d.HasLinkOrBattery));

                var page = new DevicesPage { DataContext = vm };
                var ground = new Border { Child = page };
                try
                {
                    foreach (var (width, table) in new[] { (Table, true), (NarrowList, false) })
                    {
                        Lay(ground, width);
                        PageRenderDump.Settle(dispatcher);
                        Lay(ground, width);

                        var list = (ListBox)page.FindName("MainList");
                        var rows = Under<ListBoxItem>(list).ToList();
                        Assert.Equal(vm.Devices.Select(d => d.DeviceName).OrderBy(n => n, StringComparer.Ordinal),
                            rows.Select(r => ((DeviceRowViewModel)r.DataContext).DeviceName).OrderBy(n => n, StringComparer.Ordinal));
                        Assert.All(rows, r => Assert.Equal(table, DevicesPage.GetIsWideTable(r)));

                        var forms = new List<(string Kind, double Height, double NameLeft, double NameTop, double TypeTop)>();
                        foreach (var row in rows)
                        {
                            var device = (DeviceRowViewModel)row.DataContext;
                            string what = device.DeviceTypeKey + " \"" + device.DeviceName + "\" at " + width;
                            var name = Under<TextBlock>(row).Single(t => t.Name == "RowName");
                            var type = Under<TextBlock>(row).Single(t => t.Name == "RowType");
                            Assert.Equal(device.DeviceName, name.Text);
                            Assert.Equal(device.DeviceType, type.Text);
                            Assert.False(string.IsNullOrWhiteSpace(type.Text), what + ": the row names no type");
                            // The row shows its name and its type once each.
                            Assert.Single(Under<TextBlock>(row), t => t.Text == device.DeviceName);
                            Assert.Single(Under<TextBlock>(row), t => t.Text == device.DeviceType);

                            Rect n = In(name, row), t = In(type, row);
                            Assert.True(n.Width > 0 && t.Width > 0, what + ": the name is " + n + " and the type " + t);
                            Assert.True(Math.Abs(t.Left - n.Left) < 0.01, what + ": the type starts at " + t.Left + " and the name at " + n.Left);
                            Assert.True(t.Top >= n.Bottom - 0.01, what + ": the type's top is " + t.Top + " and the name's bottom " + n.Bottom);

                            // The link and the battery: columns of the
                            // table, and after the type on its line where
                            // the list is narrow.
                            var facts = Under<StackPanel>(row).Single(p => p.Name == "FactsAfterType");
                            Assert.Equal(table ? Visibility.Collapsed : Visibility.Visible, facts.Visibility);
                            if (!table && device.HasLinkOrBattery)
                            {
                                Rect f = In(facts, row);
                                Assert.True(f.Width > 0, what + ": the facts after the type take no room");
                                Assert.True(f.Left >= t.Right - 0.01, what + ": the facts start at " + f.Left + " and the type ends at " + t.Right);
                                Assert.True(f.Top < t.Bottom && f.Bottom > t.Top, what + ": the facts at " + f + " are not on the type's line at " + t);
                            }
                            forms.Add((device.DeviceTypeKey, row.ActualHeight, Math.Round(n.Left, 2), Math.Round(n.Top, 2), Math.Round(t.Top, 2)));
                        }

                        // One form: the board's 64 for every kind, and the
                        // name and the type at one place in every row.
                        string seen = string.Join("; ", forms.Select(f => f.Kind + " " + f.Height + " tall, name at " + f.NameLeft + "," + f.NameTop + ", type at " + f.TypeTop));
                        Assert.True(forms.All(f => Math.Abs(f.Height - 64) < 0.01), "At " + width + " a row is not 64 tall: " + seen);
                        Assert.True(forms.Select(f => (f.NameLeft, f.NameTop, f.TypeTop)).Distinct().Count() == 1,
                            "At " + width + " the rows do not place the name and the type alike: " + seen);
                    }
                }
                finally
                {
                    ground.Child = null;
                }
            }, TimeSpan.FromMinutes(3), "The Devices row layout test timed out.");
        }
    }
}
