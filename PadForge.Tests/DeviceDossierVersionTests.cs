using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    /// <summary>The Device Dossier's FIRMWARE and REV lines.</summary>
    public class DeviceDossierVersionTests
    {
        /// <summary>Sony firmware reads as dualsense-tester prints the update
        /// version (high byte, dot, low byte, in hex). Microsoft and Nintendo
        /// values read as decimal major.minor (hifihedgehog/SDL#42: Xbox
        /// 5.5.2641.0 is 0x0505, a GIP hello for 5.17 is 0x0511, Switch 3.72
        /// is 0x0348). Other makers' values print as raw hex, and 0 hides the
        /// line.</summary>
        [Theory]
        [InlineData(0x0224, 0x054C, "2.24")]
        [InlineData(0x0110, 0x054C, "1.10")]
        [InlineData(0x0505, 0x045E, "5.5")]
        [InlineData(0x0511, 0x045E, "5.17")]
        [InlineData(0x0348, 0x057E, "3.72")]
        [InlineData(0x1234, 0x20D6, "0x1234")]
        [InlineData(0, 0x045E, "")]
        [InlineData(0, 0x054C, "")]
        public void Firmware_ReadsTheWayItsMakerPrintsIt(int version, int vendor, string expected)
            => Assert.Equal(expected, DeviceRowViewModel.FormatFirmwareVersion((ushort)version, (ushort)vendor));

        [Theory]
        [InlineData(0x0100, "0x0100")]
        [InlineData(0, "")]
        public void Revision_PrintsAsUsbToolsPrintIt(int version, string expected)
            => Assert.Equal(expected, DeviceRowViewModel.FormatProductRevision((ushort)version));

        /// <summary>The FIRMWARE text reads the open device's value each
        /// time, so a version SDL learns after open (a late GIP firmware
        /// reply, hifihedgehog/SDL#42) shows on the next write. A device SDL
        /// does not open has no line.</summary>
        [Fact]
        public void DossierFirmware_ReadsTheOpenDevicesValueEachTime()
        {
            var wrapper = new SdlDeviceWrapper();
            GC.SuppressFinalize(wrapper);
            var ud = new UserDevice { Device = wrapper };
            SetWrapper(wrapper, "VendorId", 0x045E);
            Assert.Equal(string.Empty, InputService.DossierFirmware(ud));
            SetWrapper(wrapper, "FirmwareVersion", 0x0505);
            Assert.Equal("5.5", InputService.DossierFirmware(ud));
            Assert.Equal(string.Empty, InputService.DossierFirmware(new UserDevice()));
            Assert.Equal(string.Empty, InputService.DossierFirmware(null));
        }

        /// <summary>The wrapper reads the version again after open, on both
        /// read paths, and the 5 s battery tick writes every row's FIRMWARE
        /// line as the row fill does. Pinned in source, since the test host
        /// opens no SDL device.</summary>
        [Fact]
        public void TheFirmwareIsReadAgainAfterOpen_AndTheBatteryTickWritesIt()
        {
            const string read = "FirmwareVersion = SDL_GetJoystickFirmwareVersion(Joystick);";
            string wrapper = RepoFile("PadForge.Engine", "Common", "SdlDeviceWrapper.cs");
            Assert.Equal(2, Regex.Matches(wrapper, Regex.Escape(read)).Count);
            int start = wrapper.IndexOf("public CustomInputState GetCurrentState(", StringComparison.Ordinal);
            int end = wrapper.IndexOf("public static Action<Guid, CustomInputState> ExternalVoiceAugment;", start, StringComparison.Ordinal);
            string readState = wrapper.Substring(start, end - start);
            Assert.Contains("now - _lastFirmwareReadTick > 5000", readState);
            Assert.Contains(read, readState);

            const string write = "row.FirmwareVersion = DossierFirmware(ud);";
            string service = RepoFile("PadForge.App", "Services", "InputService.cs");
            int tick = service.IndexOf("private void UpdateBatteryIndicators()", StringComparison.Ordinal);
            int pads = service.IndexOf("foreach (var padVm in _mainVm.Pads)", tick, StringComparison.Ordinal);
            Assert.Contains(write, service.Substring(tick, pads - tick));
            int fill = service.IndexOf("private void PopulateDeviceRow(", StringComparison.Ordinal);
            int name = service.IndexOf("row.DeviceName =", fill, StringComparison.Ordinal);
            Assert.Contains(write, service.Substring(fill, name - fill));
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
