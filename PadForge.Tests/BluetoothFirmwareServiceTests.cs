using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;

namespace PadForge.Tests
{
    /// <summary>The firmware revision a Bluetooth LE pad reports in its Device
    /// Information service, shown when SDL reports none.</summary>
    public class BluetoothFirmwareServiceTests
    {
        [Theory]
        [InlineData("a88c3e2c8a96", 0xA88C3E2C8A96UL)]
        [InlineData("A8:8C:3E:2C:8A:96", 0xA88C3E2C8A96UL)]
        [InlineData("a8-8c-3e-2c-8a-96", 0xA88C3E2C8A96UL)]
        [InlineData("a88c3e2c8a9", 0UL)]
        [InlineData("a88c3e2c8a96ff", 0UL)]
        [InlineData("not an address", 0UL)]
        [InlineData("", 0UL)]
        [InlineData(null, 0UL)]
        public void ParseAddress_ReadsTheFormWindowsWrites(string text, ulong expected)
            => Assert.Equal(expected, BluetoothFirmwareService.ParseAddress(text));

        /// <summary>The revision is UTF-8 text. Padding is cut, and anything
        /// empty, unprintable, malformed or past 64 characters is no
        /// revision.</summary>
        [Fact]
        public void Clean_KeepsTheRevisionAndCutsPadding()
        {
            Assert.Equal("5.23.6.0", BluetoothFirmwareService.Clean(Encoding.UTF8.GetBytes("5.23.6.0")));
            Assert.Equal("5.23.6.0", BluetoothFirmwareService.Clean(Encoding.UTF8.GetBytes("5.23.6.0\0\0\0")));
            Assert.Equal("5.23.6.0", BluetoothFirmwareService.Clean(Encoding.UTF8.GetBytes("5.23.6.0  ")));
            Assert.Equal(new string('5', 64), BluetoothFirmwareService.Clean(Encoding.UTF8.GetBytes(new string('5', 64))));
            Assert.Null(BluetoothFirmwareService.Clean(Encoding.UTF8.GetBytes(new string('5', 65))));
            Assert.Null(BluetoothFirmwareService.Clean(Array.Empty<byte>()));
            Assert.Null(BluetoothFirmwareService.Clean(null));
            Assert.Null(BluetoothFirmwareService.Clean(new byte[] { 0, 0 }));
            Assert.Null(BluetoothFirmwareService.Clean(Encoding.UTF8.GetBytes("5.23\u0007")));
            Assert.Null(BluetoothFirmwareService.Clean(new byte[] { 0x35, 0xFF, 0x30 }));
        }

        /// <summary>The identity struct matches OpenXInput's static asserts
        /// (OpenXinput.cpp:4106-4107): 24 bytes, generation at offset 8.</summary>
        [Fact]
        public void IdentityStruct_MatchesOpenXInputsLayout()
        {
            Assert.Equal(24, Marshal.SizeOf<BluetoothFirmwareService.OpenXInputIdentity>());
            Assert.Equal(8, (int)Marshal.OffsetOf<BluetoothFirmwareService.OpenXInputIdentity>(
                nameof(BluetoothFirmwareService.OpenXInputIdentity.generation)));
        }

        /// <summary>An XInput slot's interface is checked against the joystick
        /// through the matcher the vendor and product lookup uses, which reads
        /// every form these paths take on a Bluetooth LE Xbox pad, a classic
        /// Bluetooth DualSense and a USB pad.</summary>
        [Theory]
        [InlineData(@"\\?\HID#{00001812-0000-1000-8000-00805f9b34fb}&Dev&VID_045e&PID_0b13&REV_0523&a88c3e2c8a96&IG_00#b&892f461&1&0000#{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}", 0x045E, 0x0B13, true)]
        [InlineData(@"\\?\HID#{00001812-0000-1000-8000-00805f9b34fb}&Dev&VID_045e&PID_0b13&REV_0523&a88c3e2c8a96&IG_00#b&892f461&1&0000#{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}", 0x045E, 0x0B12, false)]
        [InlineData(@"BTHLEDEVICE\{00001812-0000-1000-8000-00805F9B34FB}_DEV_VID&02045E_PID&0B13_REV&0523_A88C3E2C8A96\A&339B5747&2&0016", 0x045E, 0x0B13, true)]
        [InlineData(@"HID\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0ce6\a&32928f16&a&0000", 0x054C, 0x0CE6, true)]
        [InlineData(@"USB\VID_045E&PID_0B12\3032363030303631", 0x045E, 0x0B12, true)]
        [InlineData(@"USB\VID_045E&PID_0B12\3032363030303631", 0x054C, 0x0B12, false)]
        [InlineData(null, 0x045E, 0x0B13, false)]
        public void NamesVidPid_ReadsEveryFormWindowsWrites(string id, int vendor, int product, bool expected)
            => Assert.Equal(expected, HidHideController.NamesVidPid(id, (ushort)vendor, (ushort)product));

        /// <summary>SDL's value wins. The Bluetooth revision is asked for only
        /// when SDL has none and the device is online.</summary>
        [Fact]
        public void DossierFirmware_TakesTheBluetoothRevision_OnlyWhenSdlHasNone()
        {
            int asked = 0;
            BluetoothFirmwareService.AnswerForTest = _ => { asked++; return "5.23.6.0"; };
            try
            {
                var wrapper = new SdlDeviceWrapper();
                GC.SuppressFinalize(wrapper);
                SetWrapper(wrapper, "VendorId", 0x045E);
                var ud = new UserDevice { Device = wrapper, IsOnline = true };
                Assert.Equal("5.23.6.0", InputService.DossierFirmware(ud));
                Assert.Equal(1, asked);

                SetWrapper(wrapper, "FirmwareVersion", 0x0505);
                Assert.Equal("5.5", InputService.DossierFirmware(ud));
                Assert.Equal(1, asked);

                SetWrapper(wrapper, "FirmwareVersion", 0);
                ud.IsOnline = false;
                Assert.Equal(string.Empty, InputService.DossierFirmware(ud));
                Assert.Equal(1, asked);

                ud.IsOnline = true;
                BluetoothFirmwareService.AnswerForTest = _ => null;
                Assert.Equal(string.Empty, InputService.DossierFirmware(ud));
            }
            finally
            {
                BluetoothFirmwareService.AnswerForTest = null;
            }
        }

        /// <summary>A joystick SDL never opened has no instance, and nothing is
        /// read for it.</summary>
        [Fact]
        public void TryGet_ReadsNothingForAJoystickSdlNeverOpened()
        {
            var wrapper = new SdlDeviceWrapper();
            GC.SuppressFinalize(wrapper);
            Assert.Null(BluetoothFirmwareService.TryGet(wrapper));
            Assert.Null(BluetoothFirmwareService.TryGet(null));
        }

        /// <summary>The pad is asked only while it is connected, so a paired
        /// pad that is off is sent nothing, and the one read that reaches it
        /// sits behind the radio fence (TestHostFenceTests). Pinned in source,
        /// since the test host opens no Bluetooth device.</summary>
        [Fact]
        public void ThePadIsAskedOnlyWhileConnected()
        {
            string source = RepoFile("PadForge.App", "Common", "Input", "BluetoothFirmwareService.cs");
            int start = source.IndexOf("private static async Task<(string Text, bool Final)> ReadRevisionAsync(ulong address)", StringComparison.Ordinal);
            int end = source.IndexOf("private static async Task<(string Text, bool Final)> ReadRevisionAsync(BluetoothLEDevice device", StringComparison.Ordinal);
            Assert.True(start > 0 && end > start);
            string body = source.Substring(start, end - start);
            const string askPad = "ReadRevisionAsync(device, BluetoothCacheMode.Uncached)";
            int connected = body.IndexOf("device.ConnectionStatus == BluetoothConnectionStatus.Connected", StringComparison.Ordinal);
            int fromPad = body.IndexOf(askPad, StringComparison.Ordinal);
            Assert.True(connected > 0 && fromPad > connected);
            Assert.Single(Regex.Matches(source, Regex.Escape(askPad)));
        }

        private static void SetWrapper(SdlDeviceWrapper wrapper, string property, int value)
            => typeof(SdlDeviceWrapper).GetProperty(property).SetValue(wrapper, (ushort)value);

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
