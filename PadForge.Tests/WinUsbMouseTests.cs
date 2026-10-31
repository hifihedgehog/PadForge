using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Common.Input.WinUsbKeyboards;
using PadForge.Engine;
using PadForge.Services;
using Xunit;
using Kind = PadForge.Services.VendorUsbDriverInstaller.OptInKind;

namespace PadForge.Tests
{
    /// <summary>
    /// The mouse switch beside #549's keyboard switch: the report decoder
    /// against Linux's mouse mapping (hid-input.c), the row a bound mouse
    /// is read through, the channel's open for a mouse, the offer on a
    /// mouse row, what leaves Windows with a mouse, and its takeover.
    /// </summary>
    public class WinUsbMouseTests
    {
        internal const string Path = @"\\?\usb#vid_046d&pid_c54d&mi_00#8&340f50e3&0&0000#{fd826c66-3556-4845-9436-0fe7580ded02}";
        internal const string Node = @"USB\VID_046D&PID_C54D&MI_00\8&340f50e3&0&0000";
        private static readonly Guid RowId = new("11111111-2222-3333-4444-555555555555");

        /// <summary>tinyusb's TUD_HID_REPORT_DESC_MOUSE (src/class/hid/hid_device.h):
        /// five buttons and three bits of padding, then X, Y and the wheel,
        /// and AC Pan on the Consumer page, each a relative byte from -127
        /// to 127.</summary>
        public static readonly byte[] BootMouse =
        {
            0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x09, 0x01, 0xA1, 0x00,
            0x05, 0x09, 0x19, 0x01, 0x29, 0x05, 0x15, 0x00, 0x25, 0x01, 0x95, 0x05, 0x75, 0x01, 0x81, 0x02,
            0x95, 0x01, 0x75, 0x03, 0x81, 0x01,
            0x05, 0x01, 0x09, 0x30, 0x09, 0x31, 0x15, 0x81, 0x25, 0x7F, 0x95, 0x02, 0x75, 0x08, 0x81, 0x06,
            0x09, 0x38, 0x15, 0x81, 0x25, 0x7F, 0x95, 0x01, 0x75, 0x08, 0x81, 0x06,
            0x05, 0x0C, 0x0A, 0x38, 0x02, 0x15, 0x81, 0x25, 0x7F, 0x95, 0x01, 0x75, 0x08, 0x81, 0x06,
            0xC0, 0xC0,
        };

        /// <summary>A gaming mouse's report 2: sixteen buttons, X and Y as
        /// 16-bit values from -32767, a wheel byte and an AC Pan byte.</summary>
        public static readonly byte[] ReportIdMouse =
        {
            0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x85, 0x02, 0x09, 0x01, 0xA1, 0x00,
            0x05, 0x09, 0x19, 0x01, 0x29, 0x10, 0x15, 0x00, 0x25, 0x01, 0x95, 0x10, 0x75, 0x01, 0x81, 0x02,
            0x05, 0x01, 0x16, 0x01, 0x80, 0x26, 0xFF, 0x7F, 0x75, 0x10, 0x95, 0x02, 0x09, 0x30, 0x09, 0x31, 0x81, 0x06,
            0x15, 0x81, 0x25, 0x7F, 0x75, 0x08, 0x95, 0x01, 0x09, 0x38, 0x81, 0x06,
            0x05, 0x0C, 0x0A, 0x38, 0x02, 0x95, 0x01, 0x81, 0x06,
            0xC0, 0xC0,
        };

        /// <summary>A pointer with absolute X and Y, as a tablet in mouse
        /// mode reports: no motion to read.</summary>
        public static readonly byte[] AbsolutePointer =
        {
            0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x09, 0x01, 0xA1, 0x00,
            0x05, 0x09, 0x19, 0x01, 0x29, 0x02, 0x15, 0x00, 0x25, 0x01, 0x95, 0x02, 0x75, 0x01, 0x81, 0x02,
            0x95, 0x01, 0x75, 0x06, 0x81, 0x01,
            0x05, 0x01, 0x09, 0x30, 0x09, 0x31, 0x15, 0x00, 0x26, 0xFF, 0x7F, 0x75, 0x10, 0x95, 0x02, 0x81, 0x02,
            0xC0, 0xC0,
        };

        /// <summary>Buttons as an array of two bytes, each 0 or a button
        /// from 1 to 5, then relative X and Y.</summary>
        public static readonly byte[] ButtonArrayMouse =
        {
            0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x09, 0x01, 0xA1, 0x00,
            0x05, 0x09, 0x19, 0x01, 0x29, 0x05, 0x15, 0x01, 0x25, 0x05, 0x95, 0x02, 0x75, 0x08, 0x81, 0x00,
            0x05, 0x01, 0x09, 0x30, 0x09, 0x31, 0x15, 0x81, 0x25, 0x7F, 0x95, 0x02, 0x75, 0x08, 0x81, 0x06,
            0xC0, 0xC0,
        };

        private static (bool Decoded, int Dx, int Dy, int Wheel, int Buttons) Read(HidMouseReports reports, byte[] report, int buttons = 0)
        {
            int dx = 0, dy = 0, wheel = 0;
            bool decoded = reports.Decode(report, ref dx, ref dy, ref wheel, ref buttons);
            return (decoded, dx, dy, wheel, buttons);
        }

        // ─── The decoder ───

        [Fact]
        public void TheBootMouse_ReadsButtonsMotionAndTheWheel()
        {
            var reports = HidMouseReports.Parse(BootMouse);
            Assert.NotNull(reports);
            Assert.False(reports.UsesReportIds);
            // Left down, five right, five up, one notch away from the user,
            // and AC Pan, which has no axis on a mouse row.
            var read = Read(reports, new byte[] { 0x01, 0x05, 0xFB, 0x01, 0x7F });
            Assert.Equal((true, 5, -5, 1, 0x01), read);
            // The buttons are a state, so a release clears them.
            Assert.Equal((true, 0, 0, 0, 0), Read(reports, new byte[] { 0x00, 0, 0, 0, 0 }, buttons: 0x01));
        }

        /// <summary>Linux gives Button page usage n the code BTN_MOUSE + n - 1
        /// for a mouse (hid-input.c:783-788): left, right, middle, side and
        /// extra, bit n - 1 of the decoded state. A sixth button has no place
        /// on a mouse row.</summary>
        [Theory]
        [InlineData(0x01, 0x01)]
        [InlineData(0x02, 0x02)]
        [InlineData(0x04, 0x04)]
        [InlineData(0x08, 0x08)]
        [InlineData(0x10, 0x10)]
        [InlineData(0x1F, 0x1F)]
        public void ButtonN_IsBitNMinusOne(byte bits, int state)
            => Assert.Equal(state, Read(HidMouseReports.Parse(BootMouse), new byte[] { bits, 0, 0, 0, 0 }).Buttons);

        [Fact]
        public void ReportIds_And16BitMotion_AreRead_AndButtonsPastFive_AreNot()
        {
            var reports = HidMouseReports.Parse(ReportIdMouse);
            Assert.NotNull(reports);
            Assert.True(reports.UsesReportIds);
            // Buttons 1, 2 and 6 down, X 0x1234, Y -0x1234, the wheel one
            // notch toward the user.
            var read = Read(reports, new byte[] { 0x02, 0x23, 0x00, 0x34, 0x12, 0xCC, 0xED, 0xFF, 0x05 });
            Assert.Equal((true, 0x1234, -0x1234, -1, 0x03), read);
        }

        [Fact]
        public void AReportOfAnotherId_OrNoBytes_ChangesNothing()
        {
            var reports = HidMouseReports.Parse(ReportIdMouse);
            Assert.Equal((false, 0, 0, 0, 0x04), Read(reports, new byte[] { 0x07, 0xFF, 0xFF, 1, 1, 1, 1, 1, 1 }, buttons: 0x04));
            Assert.Equal((false, 0, 0, 0, 0x04), Read(reports, Array.Empty<byte>(), buttons: 0x04));
        }

        /// <summary>Linux reads X and Y as motion only from a relative
        /// field (hid-input.c:908-914). A device with no relative X or Y is
        /// no mouse to read, and a keyboard is none either.</summary>
        [Fact]
        public void AnAbsolutePointer_OrAKeyboard_IsNoMouse()
        {
            Assert.Null(HidMouseReports.Parse(AbsolutePointer));
            Assert.Null(HidMouseReports.Parse(KeyboardDescriptors.Boot));
            Assert.Null(HidMouseReports.Parse(Array.Empty<byte>()));
            // And the keyboard's parser still reads keys alone.
            Assert.Null(HidKeyboardReports.Parse(KeyboardDescriptors.Mouse));
            Assert.All(HidKeyboardReports.Parse(KeyboardDescriptors.Boot).Fields, f => Assert.False(f.IsRelative));
        }

        [Fact]
        public void AButtonArray_ListsTheButtonsDown()
        {
            var reports = HidMouseReports.Parse(ButtonArrayMouse);
            Assert.NotNull(reports);
            // Buttons 1 and 3 down: left and middle.
            Assert.Equal((true, 2, -3, 0, 0x05), Read(reports, new byte[] { 0x01, 0x03, 0x02, 0xFD }));
            // An empty array releases them, and an out-of-range value is no button.
            Assert.Equal((true, 0, 0, 0, 0x00), Read(reports, new byte[] { 0x00, 0x09, 0, 0 }, buttons: 0x05));
        }

        // ─── The row ───

        private static WinUsbMouseDevice Row(Guid rowId = default)
            => new(new WinUsbKeyboard(Path, 0x046D, 0xC54D, rowId, "Gaming Mouse", IsMouse: true),
                new FakeKeyboardChannel(Path, mouse: true), start: false);

        private static Guid Md5(string text)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        /// <summary>The row is a mouse row as the Raw Input wrapper makes one
        /// (SdlMouseWrapper.cs:106-109 and :185-229): its product ID, three
        /// relative axes and five buttons with the same names, and the row
        /// ID its takeover recorded.</summary>
        [Fact]
        public void TheRow_IsAMouseRow_UnderTheRecordedId()
        {
            using var row = Row(RowId);
            Assert.Equal(RowId, row.InstanceGuid);
            Assert.Equal(Md5("Mouse"), row.ProductGuid);
            Assert.Equal(InputDeviceType.Mouse, row.GetInputDeviceType());
            Assert.Equal((3, 5), (row.NumAxes, row.NumButtons));
            Assert.Equal(new[] { "Mouse Speed X", "Mouse Speed Y", "Mouse Scroll", "Left Click", "Middle Click", "Right Click", "X1", "X2" },
                row.GetDeviceObjects().Select(o => o.Name));
            Assert.Equal("Gaming Mouse", row.Name);
            Assert.Equal(Path, row.DevicePath);

            using var unrecorded = Row();
            Assert.Equal(WinUsbMouseDevice.IdentityFor(Path), unrecorded.InstanceGuid);
            Assert.NotEqual(WinUsbKeyboardDevice.IdentityFor(Path), unrecorded.InstanceGuid);
        }

        /// <summary>HID numbers the buttons left, right, middle, side, extra.
        /// A mouse row lists left, middle, right, X1, X2.</summary>
        [Fact]
        public void TheRow_ListsLeftMiddleRightThenTheSides()
        {
            using var row = Row(RowId);
            row.Feed(new byte[] { 0x02, 0, 0, 0, 0 });
            Assert.Equal(new[] { false, false, true, false, false }, row.GetCurrentState().Buttons.Take(5));
            row.Feed(new byte[] { 0x04, 0, 0, 0, 0 });
            Assert.Equal(new[] { false, true, false, false, false }, row.GetCurrentState().Buttons.Take(5));
            row.Feed(new byte[] { 0x19, 0, 0, 0, 0 });
            Assert.Equal(new[] { true, false, false, true, true }, row.GetCurrentState().Buttons.Take(5));
        }

        /// <summary>Motion adds up between polls and starts again after one,
        /// as the Raw Input wrapper's counts do, and a notch moves the scroll
        /// axis off its center.</summary>
        [Fact]
        public void TheRow_AddsMotionBetweenPolls_AndTheWheelMovesTheScrollAxis()
        {
            using var row = Row(RowId);
            row.Feed(new byte[] { 0x00, 0x03, 0x02, 0x00, 0 });
            row.Feed(new byte[] { 0x00, 0x04, 0xFE, 0x01, 0 });
            var state = row.GetCurrentState();
            Assert.Equal((7, 0), (state.MouseRawDX, state.MouseRawDY));
            Assert.NotEqual(32767, state.Axis[2]);
            var next = row.GetCurrentState();
            Assert.Equal((0, 0), (next.MouseRawDX, next.MouseRawDY));
        }

        [Fact]
        public void ARowWhoseChannelClosed_ReturnsNoState()
        {
            var channel = new FakeKeyboardChannel(Path, mouse: true);
            using var row = new WinUsbMouseDevice(new WinUsbKeyboard(Path, 1, 2, RowId, "M", IsMouse: true), channel, start: false);
            Assert.NotNull(row.GetCurrentState());
            channel.Dispose();
            Assert.False(row.IsAttached);
            Assert.Null(row.GetCurrentState());
        }

        // ─── The channel ───

        private static FakeKeyboardUsb MouseUsb(byte[] descriptor)
        {
            // The receiver's configuration, with interface 0's report
            // descriptor as long as the one served.
            var configuration = (byte[])KeyboardDescriptors.TwoInterfaceConfiguration.Clone();
            configuration[25] = (byte)descriptor.Length;
            return new FakeKeyboardUsb { Number = 0, InPipe = 0x81, Configuration = configuration, Descriptor = descriptor };
        }

        /// <summary>A mouse's channel is read by its pointer and a keyboard's
        /// by its keys, and a descriptor with nothing of the kind asked for
        /// opens nothing and closes the interface.</summary>
        [Fact]
        public void TheChannel_OpensAMouseByItsMotion_AndAKeyboardByItsKeys()
        {
            var usb = MouseUsb(BootMouse);
            using (var channel = WinUsbKeyboardChannel.Open(Path, usb, mouse: true))
            {
                Assert.NotNull(channel);
                Assert.NotNull(channel.Pointer);
                Assert.Null(channel.Reports);
                // Its requests name interface 0.
                Assert.Contains(usb.Journal, line => line == "out 21-0A-00-00-00-00-00-00");
            }

            var keyless = MouseUsb(KeyboardDescriptors.Mouse);
            Assert.Null(WinUsbKeyboardChannel.Open(Path, keyless));
            Assert.Equal(1, keyless.Disposals);

            var keyboard = new FakeKeyboardUsb();
            Assert.Null(WinUsbKeyboardChannel.Open(Path, keyboard, mouse: true));
            Assert.Equal(1, keyboard.Disposals);
        }

        // ─── The offer, what leaves, the takeover ───

        private static readonly VendorUsbDriverInstaller.UsbNode HidUsbNode = new(Node, 0x046D, 0xC54D, 0, "HidUsb",
            new[] { @"USB\VID_046D&PID_C54D&REV_1403&MI_00", @"USB\VID_046D&PID_C54D&MI_00" },
            new[] { @"USB\Class_03&SubClass_01&Prot_02", @"USB\Class_03&SubClass_01", @"USB\Class_03" });
        private static readonly VendorUsbDriverInstaller.UsbNode WinUsbNode = HidUsbNode with { Service = "WINUSB" };
        private const string RawInputPath = @"\\?\HID#VID_046D&PID_C54D&MI_00#9&1f684252&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";

        [Fact]
        public void AMouseRow_IsOfferedTheSwitchAndTheRestore_AsAMouse()
        {
            var bind = WinUsbKeyboardBinding.QueryOffer(RawInputPath, new[] { HidUsbNode }, _ => Node, _ => false, Kind.Mouse);
            Assert.NotNull(bind);
            Assert.Equal(Kind.Mouse, bind.Kind);
            Assert.False(bind.Restore);
            Assert.Equal("Mouse read by PadForge", bind.Bind.Value.Name);
            Assert.Equal(Node, bind.NodeId);

            var restore = WinUsbKeyboardBinding.QueryOffer(Path, new[] { WinUsbNode }, _ => null, _ => true, Kind.Mouse);
            Assert.NotNull(restore);
            Assert.Equal(Kind.Mouse, restore.Kind);
            Assert.True(restore.Restore);

            // Bound one at a time, as a keyboard and a TouchSense mouse are.
            Assert.True(VendorUsbDriverInstaller.BindsOneDeviceAtATime(Kind.Mouse));
            Assert.Equal(6, (int)Kind.Mouse);
        }

        /// <summary>A taken device whose WinUSB row never opened, for a
        /// descriptor PadForge cannot read, keeps its Raw Input path. It is
        /// still offered the restore, by its takeover's node and kind, while
        /// that node is on WinUSB.</summary>
        [Fact]
        public void ATakenRowThatNeverOpened_IsStillOfferedTheRestore()
        {
            var mouse = new KeyboardTakeoverEntry { NodeId = Node, BindId = "b", RowId = RowId.ToString(), Kind = KeyboardTakeoverEntry.MouseKind };
            var offer = WinUsbKeyboardBinding.RestoreOffer(mouse, new[] { WinUsbNode });
            Assert.NotNull(offer);
            Assert.Equal((Kind.Mouse, true, Node), (offer.Kind, offer.Restore, offer.NodeId));
            Assert.Null(offer.Bind);
            var keyboard = new KeyboardTakeoverEntry { NodeId = Node, BindId = "b", RowId = RowId.ToString() };
            Assert.Equal(Kind.Keyboard, WinUsbKeyboardBinding.RestoreOffer(keyboard, new[] { WinUsbNode }).Kind);
            // Back on Windows' driver, unplugged, or no takeover: nothing.
            Assert.Null(WinUsbKeyboardBinding.RestoreOffer(mouse, new[] { HidUsbNode }));
            Assert.Null(WinUsbKeyboardBinding.RestoreOffer(mouse, Array.Empty<VendorUsbDriverInstaller.UsbNode>()));
            Assert.Null(WinUsbKeyboardBinding.RestoreOffer(null, new[] { WinUsbNode }));

            string page = AuditDelta20261002Tests.RepoText("PadForge.App", "ViewModels", "DevicesViewModel.cs");
            Assert.Contains("??Services.WinUsbKeyboardBinding.RestoreOffer(Common.Input.WinUsbKeyboards.KeyboardTakeovers.ForRow(device.InstanceGuid),nodes);",
                string.Concat(page.Where(c => !char.IsWhiteSpace(c))));
        }

        /// <summary>A mouse's own collections are the switch's subject. A
        /// keyboard on the same interface leaves Windows with it, as a mouse
        /// on a keyboard's interface leaves with the keyboard.</summary>
        [Fact]
        public void WhatLeavesWithAMouse_IsAKeyboard_NeverTheMouseItself()
        {
            Assert.Equal(WinUsbKeyboardBinding.Takes.None, WinUsbKeyboardBinding.Classify(0x01, 0x02, Kind.Mouse));
            Assert.Equal(WinUsbKeyboardBinding.Takes.None, WinUsbKeyboardBinding.Classify(0x01, 0x01, Kind.Mouse));
            Assert.Equal(WinUsbKeyboardBinding.Takes.Keyboard, WinUsbKeyboardBinding.Classify(0x01, 0x06, Kind.Mouse));
            Assert.Equal(WinUsbKeyboardBinding.Takes.Keyboard, WinUsbKeyboardBinding.Classify(0x01, 0x07, Kind.Mouse));
            Assert.Equal(WinUsbKeyboardBinding.Takes.MediaKeys, WinUsbKeyboardBinding.Classify(0x0C, 0x01, Kind.Mouse));
            // The keyboard's switch reads them the other way round.
            Assert.Equal(WinUsbKeyboardBinding.Takes.Mouse, WinUsbKeyboardBinding.Classify(0x01, 0x02));
            Assert.Equal(WinUsbKeyboardBinding.Takes.None, WinUsbKeyboardBinding.Classify(0x01, 0x06));
        }

        /// <summary>A mouse's confirm says the pointer goes, names a keyboard
        /// that shares its interface, and carries Hide from Games' warning
        /// about the only mouse, or the only keyboard or mouse when a
        /// keyboard goes along.</summary>
        [Fact]
        public void TheMouseConfirm_NamesAKeyboardThatLeaves_AndWarnsOfTheOnlyMouse()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            string plain = PadForge.MainWindow.TakeoverCost(Kind.Mouse, WinUsbKeyboardBinding.Takes.None);
            Assert.StartsWith(s.Devices_DriverConfirm_Mouse, plain);
            Assert.Contains(string.Format(s.Devices_WarnHide_Format, s.Devices_DeviceKind_Mouse), plain);
            Assert.Contains(string.Format(s.Devices_DriverKeepNote_Mouse_Format, PadForge.MainWindow.KeepDriverSeconds), plain);
            Assert.DoesNotContain(s.Devices_DriverTakes_Keyboard, plain);

            string withKeyboard = PadForge.MainWindow.TakeoverCost(Kind.Mouse,
                WinUsbKeyboardBinding.Takes.Keyboard | WinUsbKeyboardBinding.Takes.MediaKeys);
            Assert.Contains(string.Format(s.Devices_DriverTakes_Format, s.Devices_DriverTakes_Keyboard + ", " + s.Devices_DriverTakes_MediaKeys), withKeyboard);
            Assert.Contains(string.Format(s.Devices_WarnHide_Format, s.Devices_DriverKind_KeyboardOrMouse), withKeyboard);
            Assert.DoesNotContain(s.Devices_DriverConfirm_Keyboard, withKeyboard);
        }

        /// <summary>A mouse's Keep prompt speaks of the pointer in its heading
        /// and its window title, the switch hands it the kind, and the
        /// takeover it records is a mouse's.</summary>
        [Fact]
        public void AMousesSwitch_IsWordedAndRecordedAsAMouse()
        {
            string dialog = AuditDelta20261002Tests.RepoText("PadForge.App", "Views", "KeepDriverDialog.xaml.cs");
            Assert.Contains("Title = mouse ? Strings.Instance.Devices_DriverKeepTitle_Mouse : Strings.Instance.Devices_DriverKeepTitle;", dialog);
            Assert.Contains("TitleText.Text = Title;", dialog);
            Assert.Contains("? Strings.Instance.Devices_DriverKeepMessage_Mouse_Format", dialog);
            string window = AuditDelta20261002Tests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("Views.KeepDriverDialog.Show(this, name, KeepDriverSeconds, mouse)", window);
            Assert.Contains("Kind = mouse ? Common.Input.WinUsbKeyboards.KeyboardTakeoverEntry.MouseKind : null,", window);

            // The Devices page asks for a mouse row's offer as a mouse's.
            string page = AuditDelta20261002Tests.RepoText("PadForge.App", "ViewModels", "DevicesViewModel.cs");
            string flat = string.Concat(page.Where(c => !char.IsWhiteSpace(c)));
            Assert.Contains("(device.DeviceTypeKey==\"Keyboard\"||device.DeviceTypeKey==\"Mouse\")&&!device.IsAggregate&&!device.IsInternalVirtual", flat);
            Assert.Contains("kind:device.DeviceTypeKey==\"Mouse\"?Services.VendorUsbDriverInstaller.OptInKind.Mouse:Services.VendorUsbDriverInstaller.OptInKind.Keyboard", flat);
        }

        /// <summary>The takeover keeps its kind through the settings file, a
        /// keyboard's has none as before, and the look for bound devices
        /// hands the kind to the device pass.</summary>
        [Fact]
        public void AMouseTakeover_KeepsItsKind()
        {
            var entry = new KeyboardTakeoverEntry
            {
                NodeId = Node, BindId = @"USB\VID_046D&PID_C54D&MI_00", RowId = RowId.ToString(), Name = "Gaming Mouse",
                VendorId = 0x046D, ProductId = 0xC54D, Kind = KeyboardTakeoverEntry.MouseKind,
            };
            var data = new AppSettingsData { KeyboardTakeovers = new[] { entry, new KeyboardTakeoverEntry { NodeId = "n", BindId = "b", RowId = Guid.NewGuid().ToString() } } };
            var serializer = new XmlSerializer(typeof(AppSettingsData));
            using var text = new StringWriter();
            serializer.Serialize(text, data);
            var back = (AppSettingsData)serializer.Deserialize(new StringReader(text.ToString()));
            Assert.True(back.KeyboardTakeovers[0].IsMouse);
            Assert.False(back.KeyboardTakeovers[1].IsMouse);
            Assert.True(KeyboardTakeovers.Copy(entry).IsMouse);

            var bound = WinUsbKeyboardBinding.BoundKeyboards(new[] { Path }, new[] { entry });
            Assert.True(Assert.Single(bound).IsMouse);
        }
    }
}
